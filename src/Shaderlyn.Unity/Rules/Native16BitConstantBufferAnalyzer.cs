using System.Collections.Immutable;
using Shaderlyn.CFamily.Syntax;
using Shaderlyn.Core.Analysis;
using Shaderlyn.Core.Diagnostics;
using Shaderlyn.Hlsl.Parsing;
using Shaderlyn.Hlsl.Syntax;
using Shaderlyn.Rules;
using Shaderlyn.Semantics;

namespace Shaderlyn.Unity.Rules;

/// <summary>
/// 16 ビットの型を有効にした DXC に届く定数バッファのメンバーに、<c>half</c> / <c>min16float</c> などがあれば報告する (UN0205)。
/// </summary>
/// <remarks>
/// <para>
/// <c>#pragma require native16bit</c> と、<c>UNITY_DEVICE_SUPPORTS_NATIVE_16BIT</c> が有効な構成は、
/// DXC が <c>-enable-16bit-types</c> でコンパイルする。<c>half</c> は 2 バイトの <c>float16_t</c> になり、
/// 定数バッファの配置が変わる。Unity は値を 32 ビットで書き込むので、シェーダーは別の値を読む (実測)。
/// </para>
/// <para>キーワードの構成で 16 ビットの型を使わないコンパイル先にしか届かないメンバーは報告しない。</para>
/// </remarks>
internal sealed class Native16BitConstantBufferAnalyzer : SemanticRuleAnalyzer
{
    /// <inheritdoc/>
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
        [UnityRuleDescriptors.HalfInNative16BitConstantBuffer];

    /// <inheritdoc/>
    protected override void Analyze(SyntaxTreeAnalysisContext context, ShaderCompilation compilation)
    {
        if (compilation.Programs.All(p => !p.Tree.CompileTargets.Any(t => t.Dialect.Enable16BitTypes)))
        {
            return;
        }

        HashSet<int> reported = [];

        foreach ((ShaderSyntaxNode node, AnalyzedProgram program) in compilation.EnumerateRuleNodes())
        {
            if (node is not VariableDeclarationSyntax { Parent: ConstantBufferDeclarationSyntax buffer } declaration
                || !IsSixteenBitWhenEnabled(declaration.Type.NameToken.Text)
                || !compilation.IsReportable(declaration.Type))
            {
                continue;
            }

            string[] subjects =
            [
                .. CompileTargets.Reaching(compilation, declaration, program)
                    .Where(t => t.Dialect.Enable16BitTypes)
                    .Select(t => t.Subject)
                    .Distinct(StringComparer.Ordinal),
            ];

            if (subjects.Length == 0 || !reported.Add(declaration.Type.NameToken.Span.Start))
            {
                continue;
            }

            string names = string.Join(", ", declaration.Variables.Select(v => v.Name));
            string type = declaration.Type.NameToken.Text;

            context.ReportDiagnostic(Diagnostic.Create(
                UnityRuleDescriptors.HalfInNative16BitConstantBuffer,
                declaration.Type.NameToken.GetLocation(),
                $"定数バッファ{(buffer.Name.Length > 0 ? $" '{buffer.Name}'" : string.Empty)} の {names} は {type} ですが、"
                + $"{string.Join("・", subjects)} では 16 ビットの型になり、Unity が書き込んだ値を正しく読めません。"
                + $"{ThirtyTwoBit(type)} で宣言し、使うところで変換してください。"));
        }
    }

    /// <summary>同じ形の 32 ビットの型 (<c>half4</c> → <c>float4</c>、<c>min16uint2</c> → <c>uint2</c>)。</summary>
    /// <param name="name">型名。</param>
    /// <returns>32 ビットの型名。</returns>
    private static string ThirtyTwoBit(string name)
    {
        foreach ((string prefix, string replacement) in (ReadOnlySpan<(string, string)>)[("half", "float"), ("min16float", "float"), ("min16int", "int"), ("min16uint", "uint")])
        {
            if (name.StartsWith(prefix, StringComparison.Ordinal))
            {
                return replacement + name[prefix.Length..];
            }
        }

        return name;
    }

    /// <summary>16 ビットの型を有効にすると 16 ビットになる型か (<c>half</c> / <c>min16float</c> / <c>min16int</c> / <c>min16uint</c> とそのベクトル・行列)。</summary>
    /// <param name="name">型名。</param>
    /// <returns>なるなら <see langword="true"/>。</returns>
    private static bool IsSixteenBitWhenEnabled(string name)
        => name.StartsWith("half", StringComparison.Ordinal) || name.StartsWith("min16", StringComparison.Ordinal);
}
