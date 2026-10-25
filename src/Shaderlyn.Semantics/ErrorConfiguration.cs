using System.Collections.Immutable;
using Shaderlyn.CFamily.Preprocessing;
using Shaderlyn.Core.Diagnostics;
using Shaderlyn.Core.Syntax;

namespace Shaderlyn.Semantics;

/// <summary>
/// 展開が <c>#error</c> に到達した構成 1 つ。その構成はコンパイルできない。
/// </summary>
/// <param name="BlockKey">構成を展開したコードブロック (<see cref="AnalyzedProgram.BlockKey"/>)。</param>
/// <param name="EnabledSymbols">その構成で有効にしたシンボル。既定の構成なら空。</param>
/// <param name="ErrorDirectives">到達した <c>#error</c> の位置。既定の構成の木も到達するものは含まない (既定の構成でなければ)。</param>
/// <param name="IsBaseline">既定の構成か。</param>
/// <param name="BranchCondition">
/// 並べた分岐の中の <c>#error</c> なら、その分岐の条件 (<see cref="PreprocessResult.ConditionalErrorDirectives"/>)。
/// そのときは、木の構成のうちこの条件の部分だけがコンパイルできない。木そのものは使う。
/// </param>
/// <remarks>
/// <para>
/// <b>その構成はコンパイルされない (Unreal、汎用の HLSL) か、コンパイルに失敗する (Unity)。</b>
/// Unreal はその構成を C++ の <c>ShouldCompilePermutation</c> で除き、シェーダーは <c>#else</c> / <c>#error</c> で念を押す。
/// Unity は宣言したキーワードの構成をすべてコンパイルするので、到達すればシェーダーが壊れる。
/// </para>
/// <para>
/// どちらでも、その構成のコードの誤りを報告する意味は無い。宣言の有無を見る検査は、その構成を考えない
/// (<see cref="ShaderCompilation.IsPossibleIn"/>)。到達したこと自体は、環境に合わせた重要度で伝える (CF0104 / UN0103)。
/// 既定の木そのものが到達したもの (<see cref="IsBaseline"/> で <see cref="BranchCondition"/> が無いもの) は除かない。
/// 解析のマクロの状態が実際と違うためのことが多く、除くとその木のどの検査も止まる。
/// </para>
/// </remarks>
public sealed record ErrorConfiguration(
    (int Start, string? Kernel) BlockKey,
    ImmutableArray<string> EnabledSymbols,
    ImmutableArray<Location> ErrorDirectives,
    bool IsBaseline,
    SymbolCondition? BranchCondition = null);
