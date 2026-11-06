using System.Collections.Immutable;

namespace Shaderlyn.Godot;

/// <summary>
/// Godot のレンダラー (プロジェクトの設定 <c>rendering/renderer/rendering_method</c>)。
/// </summary>
/// <remarks>
/// シェーダーのコンパイラ (Godot 4.7.2 の <c>ShaderLanguage</c> と <c>ShaderPreprocessor</c>) は、次のものをレンダラーで変える。
/// <list type="bullet">
///   <item><description>プリプロセッサの <c>CURRENT_RENDERER</c> の値</description></item>
///   <item><description>Compatibility (<c>is_low_end</c>) では high-end の組み込み関数 (<c>textureGather</c> など) を使えない</description></item>
///   <item><description><c>hint_normal_roughness_texture</c> は Forward+ でだけ使える</description></item>
///   <item><description>varying の数の上限。Forward+ と Mobile は、組み込みの varying の分だけ利用者の varying の番号をずらす (<see cref="GodotRenderers.BaseVaryingIndex"/>)</description></item>
/// </list>
/// </remarks>
internal enum GodotRenderer
{
    /// <summary>Forward+ (<c>forward_plus</c>)。Godot の既定。</summary>
    ForwardPlus,

    /// <summary>Mobile (<c>mobile</c>)。</summary>
    Mobile,

    /// <summary>Compatibility (<c>gl_compatibility</c>)。OpenGL で描画する。</summary>
    Compatibility,
}

/// <summary><see cref="GodotRenderer"/> の値ごとの性質。</summary>
internal static class GodotRenderers
{
    /// <summary>
    /// varying の数の上限 (<c>get_maximum_shader_varyings</c>)。
    /// </summary>
    /// <remarks>
    /// 本来は GPU で決まる。Vulkan と OpenGL は頂点の出力とフラグメントの入力の成分の数の小さい方を 4 で割ったもの、D3D12 は 32、Metal は 31。
    /// デスクトップの GPU では Vulkan・D3D12・OpenGL のどれも 32 になる (Godot 4.7.2 で確かめた) ので、32 とする。
    /// 上限の小さい GPU (Metal・携帯機器) では、Godot だけが誤りにすることがある。
    /// </remarks>
    public const int MaxVaryings = 32;

    /// <summary>設定の名前 (<c>forward_plus</c> など) からレンダラーを読む。</summary>
    /// <param name="name">設定の名前。</param>
    /// <param name="renderer">読んだレンダラー。</param>
    /// <returns>知っている名前なら <see langword="true"/>。</returns>
    public static bool TryParse(string? name, out GodotRenderer renderer)
    {
        switch (name)
        {
            case "forward_plus":
                renderer = GodotRenderer.ForwardPlus;
                return true;

            case "mobile":
                renderer = GodotRenderer.Mobile;
                return true;

            case "gl_compatibility":
                renderer = GodotRenderer.Compatibility;
                return true;

            default:
                renderer = GodotRenderer.ForwardPlus;
                return false;
        }
    }

    /// <summary>Godot のエディターでの名前。</summary>
    /// <param name="renderer">レンダラー。</param>
    /// <returns>Forward+ / Mobile / Compatibility。</returns>
    public static string DisplayName(this GodotRenderer renderer) => renderer switch
    {
        GodotRenderer.Mobile => "Mobile",
        GodotRenderer.Compatibility => "Compatibility",
        _ => "Forward+",
    };

    /// <summary>high-end の組み込み関数を使えないレンダラーか (<c>RenderingServer::is_low_end</c>)。</summary>
    /// <param name="renderer">レンダラー。</param>
    /// <returns>Compatibility なら <see langword="true"/>。</returns>
    public static bool IsLowEnd(this GodotRenderer renderer) => renderer == GodotRenderer.Compatibility;

    /// <summary>
    /// 利用者の varying の最初の番号 (<c>base_varying_index</c>)。組み込みの varying が前の番号を使う。
    /// </summary>
    /// <param name="renderer">レンダラー。</param>
    /// <param name="shaderType">シェーダーの種類。</param>
    /// <returns>Forward+ と Mobile の spatial は 15、canvas_item は 9。そのほかは 0。</returns>
    public static int BaseVaryingIndex(this GodotRenderer renderer, string? shaderType) => (renderer, shaderType) switch
    {
        (GodotRenderer.ForwardPlus or GodotRenderer.Mobile, "spatial") => 15,
        (GodotRenderer.ForwardPlus or GodotRenderer.Mobile, "canvas_item") => 9,
        _ => 0,
    };

    /// <summary>
    /// Godot のプリプロセッサが定義するマクロ (<c>CURRENT_RENDERER</c> と、それと比べる <c>RENDERER_*</c>)。
    /// </summary>
    /// <param name="renderer">レンダラー。</param>
    /// <returns>マクロの名前と値。</returns>
    public static ImmutableDictionary<string, string> CreateBuiltinMacros(this GodotRenderer renderer)
        => ImmutableDictionary.CreateRange(StringComparer.Ordinal,
        [
            new KeyValuePair<string, string>("CURRENT_RENDERER", renderer switch
            {
                GodotRenderer.Compatibility => "0",
                GodotRenderer.Mobile => "1",
                _ => "2",
            }),
            new KeyValuePair<string, string>("RENDERER_COMPATIBILITY", "0"),
            new KeyValuePair<string, string>("RENDERER_MOBILE", "1"),
            new KeyValuePair<string, string>("RENDERER_FORWARD_PLUS", "2"),
        ]);
}
