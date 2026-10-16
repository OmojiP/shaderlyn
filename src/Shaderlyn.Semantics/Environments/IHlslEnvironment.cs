using Shaderlyn.CFamily.Preprocessing;
using Shaderlyn.CFamily.Syntax;
using Shaderlyn.Core.Analysis;
using Shaderlyn.Core.Text;
using Shaderlyn.Hlsl;

namespace Shaderlyn.Semantics.Environments;

/// <summary>
/// HLSL を使う環境 (Unity、Unreal、汎用の HLSL、独自エンジン)。
/// </summary>
/// <remarks>
/// 言語に依らない部分 (<see cref="IShaderEnvironment"/>) に、HLSL として読むことを足す。
/// 環境を作る人は、この型を実装すれば HLSL の構文解析を書かずに済む。
/// </remarks>
public interface IHlslEnvironment : IShaderEnvironment
{
    /// <inheritdoc/>
    ShaderSyntaxTree IShaderEnvironment.Parse(SourceText text, PreprocessorOptions options, ISyntaxTimingRecorder? timings)
        => HlslSyntaxTree.Parse(text, options, timings);

    /// <inheritdoc/>
    Symbols.ShaderTypeSystem IShaderEnvironment.TypeSystem => Symbols.HlslTypeSystem.Instance;
}
