namespace Shaderlyn.Semantics.Analysis;

/// <summary>
/// ルールを当てるシェーダーの言語 (<see cref="ShaderRuleAnalyzer.Languages"/>)。組み合わせて指定できる。
/// </summary>
/// <remarks>
/// 環境 (<c>environment</c>) と言語の対応は次のとおり。
/// <c>unity</c>・<c>hlsl</c>・<c>unreal</c> と独自エンジンの HLSL の環境は <see cref="Hlsl"/>、
/// <c>glsl</c> は <see cref="Glsl"/>、<c>godot</c> は <see cref="Godot"/>、<c>slang</c> は <see cref="Slang"/>、<c>wgsl</c> は <see cref="Wgsl"/>。
/// </remarks>
[Flags]
public enum ShaderLanguages
{
    /// <summary>どの言語にも当てない。</summary>
    None = 0,

    /// <summary>HLSL (Unity の埋め込みのコードと <c>.hlsl</c>・<c>.compute</c>、汎用の HLSL、Unreal Engine)。</summary>
    Hlsl = 1,

    /// <summary>GLSL (<c>.vert</c>・<c>.frag</c>・<c>.comp</c>・<c>.glsl</c> など)。</summary>
    Glsl = 2,

    /// <summary>Godot のシェーダー言語 (<c>.gdshader</c>・<c>.gdshaderinc</c>)。</summary>
    Godot = 4,

    /// <summary>Slang (<c>.slang</c>)。</summary>
    Slang = 8,

    /// <summary>WGSL (<c>.wgsl</c>)。</summary>
    Wgsl = 16,

    /// <summary>すべての言語。</summary>
    All = Hlsl | Glsl | Godot | Slang | Wgsl,
}
