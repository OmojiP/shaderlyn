using System.Collections.Immutable;
using Shaderlyn.Core.Analysis;
using Shaderlyn.Rules;

namespace Shaderlyn.Cli;

/// <summary>
/// このツールに組み込まれているアナライザの一覧。
/// </summary>
/// <remarks>
/// <para>
/// <b>ルールを追加したら必ずこの配列に登録すること。</b>
/// Native AOT ではアセンブリをリフレクションで走査して
/// <see cref="DiagnosticAnalyzer"/> の派生型を自動収集することができないため、
/// 静的な配列として明示的に列挙する必要がある。
/// </para>
/// <para>
/// 登録漏れは人間が気づきにくいので、テスト側でリフレクションを使って
/// 「アセンブリ内の全 <see cref="DiagnosticAnalyzer"/> 派生型がここに含まれているか」を検証している。
/// テストは AOT の制約を受けないため、リフレクションを使ってよい。
/// </para>
/// </remarks>
public static class BuiltInAnalyzers
{
    /// <summary>
    /// 組み込みアナライザの全一覧。
    /// </summary>
    public static ImmutableArray<DiagnosticAnalyzer> All { get; } =
    [
        // ShaderLab 単体で完結するルール。
        new TagAnalyzer(),
        new PassNameAnalyzer(),
        new TransparencyAnalyzer(),
        new RenderStateValueAnalyzer(),
        new PropertyNamingAnalyzer(),
        new PropertyAttributeAnalyzer(),
        new ShaderNameAnalyzer(),
        new UnanalyzedBlockAnalyzer(),

        // ShaderLab と HLSL を突き合わせて初めて検査できるルール。
        // セマンティックモデルが渡されていない場合、これらは何も報告しない。
        new SkippedCheckAnalyzer(),
        new UnexploredSymbolAnalyzer(),
        new UnresolvedConditionAnalyzer(),
        new PropertyUniformAnalyzer(),
        new UnusedPropertyAnalyzer(),
        new MaterialConstantBufferAnalyzer(),

        // HLSL のコードを対象にするルール。
        new DuplicateSemanticAnalyzer(),
        new ShaderSymbolAnalyzer(),
        new IncludePathAnalyzer(),
        new EntryPointAnalyzer(),
        new FunctionDefinitionAnalyzer(),
        new VariableDeclarationPresenceAnalyzer(),
        new MemberAccessAnalyzer(),
        new CallSignatureAnalyzer(),
        new ValueConversionAnalyzer(),
        new UndeclaredIdentifierAnalyzer(),
        new RedeclarationAnalyzer(),
        new DeclarationSyntaxAnalyzer(),

        // レンダーパイプライン固有のルール。
        new SrpBatcherAnalyzer(),
        new ObsoleteApiAnalyzer(),

        // 設定ファイルに書かれたプロジェクト固有のルール。
        // 定義が無ければ何も報告しない。
    ];
}
