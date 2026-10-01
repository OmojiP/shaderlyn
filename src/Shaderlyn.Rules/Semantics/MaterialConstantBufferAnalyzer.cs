using System.Collections.Immutable;
using Shaderlyn.Core.Analysis;
using Shaderlyn.Core.Diagnostics;
using Shaderlyn.Semantics;
using Shaderlyn.Semantics.Symbols;

namespace Shaderlyn.Rules;

/// <summary>
/// テクスチャプロパティに対して Unity が自動的に用意する uniform を判別する。
/// </summary>
/// <remarks>
/// <para>
/// <c>2D</c> のプロパティ <c>_BaseMap</c> を宣言すると、Unity は
/// <c>_BaseMap_ST</c> (スケールとオフセット) を自動的に埋める。
/// これらは <c>Properties</c> に書かないのが正しく、
/// 「Properties に対応するものが無い」と指摘してはならない。
/// </para>
/// <para>
/// <b>接尾辞だけで判定してはならない。</b>
/// <c>_Foo_ST</c> という名前の uniform があっても、
/// <c>_Foo</c> というテクスチャプロパティが実在しなければ自動生成されない。
/// 対応するテクスチャプロパティの存在まで確かめる必要がある。
/// </para>
/// </remarks>
internal static class AutoProvidedUniforms
{
    /// <summary>テクスチャプロパティに付随して自動的に用意される uniform の接尾辞。</summary>
    private static readonly string[] Suffixes =
    [
        // スケールとオフセット。TRANSFORM_TEX が参照する。
        "_ST",

        // 幅・高さとその逆数。
        "_TexelSize",

        // HDR テクスチャのデコード情報。
        "_HDR",
    ];

    /// <summary>
    /// uniform がテクスチャプロパティに付随して自動的に用意されるものかを判定する。
    /// </summary>
    /// <param name="uniformName">uniform の名前。</param>
    /// <param name="properties">シェーダーのプロパティ一覧。</param>
    /// <returns>自動的に用意されるものであれば <see langword="true"/>。</returns>
    public static bool IsAutoProvided(string uniformName, ImmutableArray<PropertySymbol> properties)
    {
        foreach (string suffix in Suffixes)
        {
            if (!uniformName.EndsWith(suffix, StringComparison.Ordinal))
            {
                continue;
            }

            string baseName = uniformName[..^suffix.Length];

            foreach (PropertySymbol property in properties)
            {
                if (property.Kind.IsTexture() && string.Equals(property.Name, baseName, StringComparison.Ordinal))
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>
    /// Unity エンジンが値を与える組み込み uniform かを判定する。
    /// </summary>
    /// <param name="uniformName">uniform の名前。</param>
    /// <returns>組み込み uniform であれば <see langword="true"/>。</returns>
    /// <remarks>
    /// Unity の組み込み変数は <c>unity_</c> 接頭辞を使う規約になっている。
    /// マテリアルではなくエンジンが値を与えるため、Properties への公開は不要である。
    /// </remarks>
    public static bool IsEngineProvided(string uniformName)
        => uniformName.StartsWith("unity_", StringComparison.Ordinal);
}

/// <summary>
/// マテリアル定数バッファの uniform が Properties に公開されているかを検査する (SL1002)。
/// </summary>
/// <remarks>
/// <para>
/// <b>検査対象をマテリアル用の定数バッファに限っている。</b>
/// HLSL の大域変数一般を対象にすると、スクリプトから設定する大域 uniform
/// (<c>_GlobalWindDirection</c> のようなもの) がすべて指摘され、誤検出の山になる。
/// </para>
/// <para>
/// マテリアル用の定数バッファに置かれた値は、定義上「マテリアルごとの値」である。
/// それが Properties に無いということは、インスペクタからは触れないということであり、
/// 指摘する意味がある。とはいえスクリプトから設定する内部状態という正当な用途もあるため、
/// 既定の重要度は情報にとどめている。
/// </para>
/// </remarks>
internal sealed class MaterialConstantBufferAnalyzer : SemanticRuleAnalyzer
{
    /// <inheritdoc/>
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
        [SemanticRuleDescriptors.UniformNotExposed];

    /// <inheritdoc/>
    /// <remarks>
    /// <para>
    /// <b>報告するのは、この <c>.shader</c> 自身が書いた宣言だけである。</b>
    /// 定数バッファを共有ヘッダで定義し、複数のシェーダーから取り込む構成は普通にある。
    /// その場合、バッファの中身はこのシェーダーの <c>Properties</c> とは関係が無い。
    /// ヘッダ側の宣言を指摘しても、そのヘッダを共有する他のシェーダーのために
    /// 変えられないので、直しようがない。
    /// </para>
    /// <para>
    /// <c>Properties</c> を 1 つも持たないシェーダーも対象外とする。
    /// 描画の下請けとして使われるシェーダーであり、
    /// バッファの中身をすべて「公開されていない」と報告しても雑音にしかならない。
    /// </para>
    /// </remarks>
    protected override void Analyze(SyntaxTreeAnalysisContext context, ShaderCompilation compilation)
    {
        if (!compilation.HasCompleteDependencies
            || !compilation.Profile.AppliesTo(compilation)
            || compilation.Properties.IsEmpty)
        {
            return;
        }

        HashSet<string> propertyNames =
            new(compilation.Properties.Select(p => p.Name), StringComparer.Ordinal);

        // 同じ定数バッファが複数の Pass に現れるため、同じ uniform を重ねて報告しないようにする。
        HashSet<string> reported = new(StringComparer.Ordinal);

        foreach (ConstantBufferSymbol buffer in compilation.GetMaterialConstantBuffers())
        {
            foreach (UniformSymbol member in buffer.Members)
            {
                if (propertyNames.Contains(member.Name)
                    || AutoProvidedUniforms.IsEngineProvided(member.Name)
                    || AutoProvidedUniforms.IsAutoProvided(member.Name, compilation.Properties)
                    || !reported.Add(member.Name))
                {
                    continue;
                }

                // 定数バッファを利用者が書いたヘッダ (シェーダーごとの入力ファイル) に置く書き方も多い。
                if (member.GetLocation() is { } location && compilation.IsUserFile(location.FilePath))
                {
                    context.ReportDiagnostic(Diagnostic.Create(
                        SemanticRuleDescriptors.UniformNotExposed, location, member.Name, buffer.Name));
                }
            }
        }
    }
}
