using Shaderlyn.Semantics;

namespace Shaderlyn.Unity;

/// <summary>
/// セマンティックモデルから Unity の情報を引く。
/// </summary>
public static class UnityCompilationExtensions
{
    /// <summary>
    /// Unity のシェーダーとしての情報を返す。
    /// </summary>
    /// <param name="compilation">セマンティックモデル。</param>
    /// <returns>Unity の情報。Unity の環境で作ったモデルでなければ <see langword="null"/>。</returns>
    public static UnityShaderModel? GetUnity(this ShaderCompilation compilation)
    {
        ArgumentNullException.ThrowIfNull(compilation);
        return compilation.Source.Data as UnityShaderModel;
    }

    /// <summary>
    /// 木を解析したコードブロックの、Unity としての情報を返す。
    /// </summary>
    /// <param name="program">解析した木。</param>
    /// <returns>ブロックの情報。Unity の環境で作った木でなければ <see langword="null"/>。</returns>
    public static UnityProgramBlock? GetUnityBlock(this AnalyzedProgram program)
    {
        ArgumentNullException.ThrowIfNull(program);
        return program.Unit.Data as UnityProgramBlock;
    }
}
