using System.Collections.Immutable;
using Shaderlyn.Core.Analysis;
using Shaderlyn.Core.Diagnostics;
using Shaderlyn.Hlsl.Syntax;
using Shaderlyn.Semantics;
using Shaderlyn.Semantics.Symbols;

namespace Shaderlyn.Rules;

/// <summary>
/// SRP Batcher との互換性を検査する (URP0001 / URP0002)。
/// </summary>
/// <remarks>
/// <para>
/// SRP Batcher は「マテリアルごとの値がすべて所定の定数バッファに収まっている」ことを前提に、
/// 描画呼び出しをまとめる。1 つでも外れているとそのシェーダーはバッチの対象外になる。
/// </para>
/// <para>
/// <b>この不具合はエラーとして現れない。</b>
/// コンパイルは通り、描画結果も変わらず、ただ描画呼び出しが増えて遅くなるだけである。
/// フレームデバッガを開いて初めて気づく種類の問題であり、
/// だからこそ静的解析で先に見つける価値がある。
/// </para>
/// </remarks>
internal sealed class SrpBatcherAnalyzer : SemanticRuleAnalyzer
{
    /// <inheritdoc/>
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
    [
        RenderPipelineRuleDescriptors.SrpBatcherIncompatible,
        RenderPipelineRuleDescriptors.TextureInConstantBuffer,
    ];

    /// <inheritdoc/>
    protected override void Analyze(SyntaxTreeAnalysisContext context, ShaderCompilation compilation)
    {
        if (compilation.Profile.MaterialConstantBufferName is not { } bufferName
            || !compilation.HasCompleteDependencies
            || !compilation.Profile.AppliesTo(compilation))
        {
            return;
        }

        ImmutableArray<ConstantBufferSymbol> buffers = compilation.GetMaterialConstantBuffers();

        // 定数バッファが 1 つも無いシェーダーは、そもそも SRP Batcher を狙っていない。
        // 「全プロパティが入っていない」と全件報告しても、直し方の指示にはならず雑音になる。
        // 対応させるかどうかはシェーダーの設計判断であり、ルールが決めることではない。
        if (buffers.IsEmpty)
        {
            return;
        }

        CheckPropertiesAreInBuffer(context, compilation, bufferName);
        CheckBufferContainsOnlyNumericValues(context, buffers, bufferName);
    }

    /// <summary>
    /// マテリアルの uniform が定数バッファの中で宣言されているかを検査する (URP0001)。
    /// </summary>
    /// <param name="context">解析コンテキスト。</param>
    /// <param name="compilation">対象シェーダーのセマンティックモデル。</param>
    /// <param name="bufferName">定数バッファの名前。</param>
    /// <remarks>
    /// <para>
    /// <b>検査するのは「uniform の宣言がどこにあるか」であって、
    /// 「Properties の項目が定数バッファに列挙されているか」ではない。</b>
    /// </para>
    /// <para>
    /// SRP Batcher が見ているのは HLSL の uniform 宣言である。
    /// <c>Properties</c> にあっても HLSL で uniform を宣言していない項目は、
    /// バインドされる値を持たないので互換性に関係しない。
    /// Unity 自身のシェーダーには、マテリアルエディタの表示を制御するためだけの
    /// <c>Properties</c> 項目が多数あり、これらを指摘するのは誤りである。
    /// </para>
    /// <para>
    /// 逆に、uniform として宣言されているのに定数バッファの外にあるものは、
    /// 「マテリアルごとの値がバッファの外にある」状態そのものであり、
    /// これがバッチを無効にする。
    /// </para>
    /// <para>
    /// テクスチャとサンプラは定数バッファに入れられないため対象外とする。
    /// <c>[PerRendererData]</c> が付いたプロパティも、
    /// <c>MaterialPropertyBlock</c> から値が来るため定数バッファに入れてはならない。
    /// </para>
    /// </remarks>
    private static void CheckPropertiesAreInBuffer(
        SyntaxTreeAnalysisContext context,
        ShaderCompilation compilation,
        string bufferName)
    {
        foreach (PropertySymbol property in compilation.Properties)
        {
            if (property.IsPerRendererData())
            {
                continue;
            }

            if (FindUniformOutsideBuffer(compilation, property.Name, bufferName))
            {
                context.ReportDiagnostic(
                    RenderPipelineRuleDescriptors.SrpBatcherIncompatible,
                    property.Declaration.NameToken.Span,
                    property.Name,
                    bufferName);
            }
        }
    }

    /// <summary>
    /// どれかのコードブロックで、その名前の uniform が定数バッファの外に宣言されているかを判定する。
    /// </summary>
    /// <param name="compilation">対象シェーダーのセマンティックモデル。</param>
    /// <param name="name">プロパティの名前。</param>
    /// <param name="bufferName">定数バッファの名前。</param>
    /// <returns>外にあるブロックがあれば <see langword="true"/>。</returns>
    /// <remarks>
    /// <para>
    /// <b>Pass ごとに判定する。</b> SRP Batcher は Pass ごとに定数バッファの形を見る。
    /// 1 つの Pass でも外にあれば互換性は崩れる。
    /// </para>
    /// <para>
    /// そのブロックの別の構成でだけ定数バッファに入る書き方をしている可能性がある。
    /// そのブロックの読み飛ばした分岐やマクロの本体に名前があれば見送る。
    /// 別の Pass の読み飛ばしを根拠にしない (<see cref="ShaderCompilation.AppearsOutsideAnalyzedCode(string, AnalyzedProgram)"/>)。
    /// </para>
    /// </remarks>
    private static bool FindUniformOutsideBuffer(ShaderCompilation compilation, string name, string bufferName)
    {
        foreach (AnalyzedProgram program in compilation.Programs.Concat(compilation.SymbolVariants))
        {
            if (compilation.AppearsOutsideAnalyzedCode(name, program))
            {
                continue;
            }

            foreach (UniformSymbol uniform in program.Uniforms)
            {
                // テクスチャとサンプラは定数バッファに入れられない。外にあるのが正しい。
                if (string.Equals(uniform.Name, name, StringComparison.Ordinal)
                    && uniform.TypeClass.IsNumeric()
                    && !uniform.IsInBuffer(bufferName))
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>
    /// 定数バッファにテクスチャやサンプラが入っていないかを検査する (URP0002)。
    /// </summary>
    /// <param name="context">解析コンテキスト。</param>
    /// <param name="buffers">マテリアル用の定数バッファ。</param>
    /// <param name="bufferName">定数バッファの名前。</param>
    private static void CheckBufferContainsOnlyNumericValues(
        SyntaxTreeAnalysisContext context,
        ImmutableArray<ConstantBufferSymbol> buffers,
        string bufferName)
    {
        // 同じ定数バッファが複数の Pass に現れるため、同じ uniform を重ねて報告しないようにする。
        HashSet<string> reported = new(StringComparer.Ordinal);

        foreach (ConstantBufferSymbol buffer in buffers)
        {
            foreach (UniformSymbol member in buffer.Members)
            {
                if (!member.TypeClass.IsTextureOrSampler() || !reported.Add(member.Name))
                {
                    continue;
                }

                if (member.GetLocation() is { } location)
                {
                    context.ReportDiagnostic(Diagnostic.Create(
                        RenderPipelineRuleDescriptors.TextureInConstantBuffer,
                        location,
                        member.Name,
                        bufferName));
                }
            }
        }
    }
}

/// <summary>
/// そのレンダーパイプラインで推奨されない API の使用を検査する (URP0003)。
/// </summary>
/// <remarks>
/// <para>
/// <b>マクロ展開前のコードを見る。</b>
/// <c>UNITY_MATRIX_MVP</c> のようにマクロ自体を対象にしたい場合、
/// 展開後のトークン列にはその名前が残っておらず、検出しようがない。
/// </para>
/// <para>
/// <b>プロファイルの指定だけでは発火しない。</b>
/// 対象のシェーダー自身が SRP のヘッダを取り込んでいることを確認してから適用する。
/// Built-in 用のシェーダーにとっては <c>UnityObjectToClipPos</c> が正規の書き方であり、
/// それを指摘するのは端的に誤りである。
/// </para>
/// </remarks>
internal sealed class ObsoleteApiAnalyzer : SemanticRuleAnalyzer
{
    /// <inheritdoc/>
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
        [RenderPipelineRuleDescriptors.ObsoleteApi];

    /// <inheritdoc/>
    protected override void Analyze(SyntaxTreeAnalysisContext context, ShaderCompilation compilation)
    {
        ImmutableDictionary<string, string> obsolete = compilation.Profile.ObsoleteApis;

        if (obsolete.IsEmpty || !compilation.Profile.AppliesTo(compilation))
        {
            return;
        }

        // 利用者が書いて取り込んだヘッダの中の呼び出しも、利用者が直すコードである。
        foreach (HlslSyntaxToken token in compilation.CodeTokens.Concat(compilation.UserIncludeCodeTokens))
        {
            if (token.Kind != HlslSyntaxKind.IdentifierToken
                || !obsolete.TryGetValue(token.Text, out string? replacement))
            {
                continue;
            }

            context.ReportDiagnostic(Diagnostic.Create(
                RenderPipelineRuleDescriptors.ObsoleteApi,
                token.GetLocation(),
                token.Text,
                compilation.Profile.DisplayName,
                replacement));
        }
    }
}
