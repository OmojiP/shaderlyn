using Shaderlyn.Core.Diagnostics;

namespace Shaderlyn.Semantics.Environments;

/// <summary>
/// コンパイル単位のエントリポイント 1 つ。
/// </summary>
/// <param name="Name">関数の名前。</param>
/// <param name="Origin">
/// どこで指定されたかを読み手に伝える語。診断の文に入る
/// (Unity なら <c>#pragma vertex</c>、設定ファイルで指定したなら <c>設定ファイルの entry</c>)。
/// </param>
/// <param name="Location">
/// 名前が書かれた位置。設定ファイルなどソースの外で指定したものは <see langword="null"/> で、
/// 単位全体の位置 (<see cref="AnalyzedProgram.GetBlockLocation"/>) に報告する。
/// </param>
/// <remarks>
/// 環境が読む (<see cref="IShaderEnvironment.ReadEntryPoints"/>)。解析は、この名前の関数があるかだけを見る (HL0301)。
/// </remarks>
public sealed record ShaderEntryPoint(string Name, string Origin, Location? Location = null);
