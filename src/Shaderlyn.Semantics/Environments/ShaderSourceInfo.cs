using System.Collections.Immutable;
using Shaderlyn.Core.Diagnostics;
using Shaderlyn.Core.Syntax;

namespace Shaderlyn.Semantics.Environments;

/// <summary>
/// 環境が解析した、ファイルそのものの情報。
/// </summary>
/// <param name="Root">
/// ファイル全体の構文木の根。アナライザがノードを歩く対象になる。
/// Unity の <c>.shader</c> なら ShaderLab の木。ファイル全体の木を持たない環境では <see langword="null"/>。
/// </param>
/// <param name="Diagnostics">その構文木の診断。</param>
/// <param name="Data">環境がファイルに持たせる情報。解析そのものは中身を見ない。</param>
/// <remarks>
/// <b>コンパイル単位 (<see cref="HlslCompilationUnit"/>) の外側にある情報である。</b>
/// Unity の <c>Properties</c> や <c>Pass</c> の設定は、どの単位の HLSL にも含まれない。
/// </remarks>
public sealed record ShaderSourceInfo(
    SyntaxNode? Root,
    ImmutableArray<Diagnostic> Diagnostics,
    object? Data = null)
{
    /// <summary>何も持たない情報。</summary>
    public static ShaderSourceInfo None { get; } = new(null, []);
}
