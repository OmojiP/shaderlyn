using System.Collections.Frozen;
using System.Collections.Immutable;
using Shaderlyn.Core.Analysis;
using Shaderlyn.Core.Diagnostics;
using Shaderlyn.Core.Syntax;
using Shaderlyn.Hlsl.Preprocessing;
using Shaderlyn.Hlsl.Syntax;
using Shaderlyn.Semantics;
using Shaderlyn.Semantics.Conditional;

namespace Shaderlyn.Rules;

/// <summary>
/// 構造体の中でセマンティクスが重複していないかを検査する (HL0210)。
/// </summary>
/// <remarks>
/// <b>検査するのは、そのシェーダー自身が書いた構造体だけである。</b>
/// Unity のヘッダで定義された構造体を指摘しても直しようがない。
/// </remarks>
internal sealed class DuplicateSemanticAnalyzer : SemanticRuleAnalyzer
{
    /// <summary>
    /// セマンティクスではない修飾。
    /// </summary>
    /// <remarks>
    /// <c>: register(t0)</c> や <c>: packoffset(c0)</c> は構文上セマンティクスと同じ形をしているが、
    /// 意味が違うので重複の検査から外す。
    /// これらは引数を伴うので、引数の有無で見分けられる。
    /// </remarks>
    private static readonly FrozenSet<string> NonSemanticAnnotations =
        new[] { "register", "packoffset" }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    /// <inheritdoc/>
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
        [HlslRuleDescriptors.DuplicateSemantic];

    /// <inheritdoc/>
    protected override void Analyze(SyntaxTreeAnalysisContext context, ShaderCompilation compilation)
    {
        // 同じ構造体が複数の Pass で解析されるため、重ねて報告しないようにする。
        HashSet<string> reported = new(StringComparer.Ordinal);

        foreach (AnalyzedProgram program in compilation.Programs)
        {
            foreach (StructDeclarationSyntax declaration in program.Structs)
            {
                AnalyzeStruct(context, compilation, declaration, reported);
            }
        }
    }

    /// <summary>構造体 1 つ分を検査する。</summary>
    /// <param name="context">解析コンテキスト。</param>
    /// <param name="compilation">対象シェーダーのセマンティックモデル。</param>
    /// <param name="declaration">検査する構造体。</param>
    /// <param name="reported">報告済みの組み合わせ。</param>
    private static void AnalyzeStruct(
        SyntaxTreeAnalysisContext context,
        ShaderCompilation compilation,
        StructDeclarationSyntax declaration,
        HashSet<string> reported)
    {
        // 対象は利用者が書いた構造体 (このシェーダーと、利用者が書いて取り込んだヘッダ) に限る。
        // Unity のヘッダの構造体は数百に及ぶため、先に絞り込む。
        if (declaration.GetLocation() is not { } declarationLocation
            || !compilation.IsUserFile(declarationLocation.FilePath))
        {
            return;
        }

        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);

        foreach (VariableDeclarationSyntax field in declaration.Fields)
        {
            foreach (VariableDeclaratorSyntax variable in field.Variables)
            {
                foreach (SemanticSyntax semantic in variable.Semantics)
                {
                    if (semantic.HasArguments
                        || NonSemanticAnnotations.Contains(semantic.Name)
                        || semantic.NameToken.IsMissing)
                    {
                        continue;
                    }

                    if (seen.Add(semantic.Name))
                    {
                        continue;
                    }

                    ReportDuplicate(context, compilation, declaration, semantic, reported);
                }
            }
        }
    }

    /// <summary>重複を報告する。</summary>
    /// <param name="context">解析コンテキスト。</param>
    /// <param name="compilation">対象シェーダーのセマンティックモデル。</param>
    /// <param name="declaration">対象の構造体。</param>
    /// <param name="semantic">重複しているセマンティクス。</param>
    /// <param name="reported">報告済みの組み合わせ。</param>
    private static void ReportDuplicate(
        SyntaxTreeAnalysisContext context,
        ShaderCompilation compilation,
        StructDeclarationSyntax declaration,
        SemanticSyntax semantic,
        HashSet<string> reported)
    {
        if (semantic.GetLocation() is not { } location)
        {
            return;
        }

        // 同じ構造体が複数の Pass で解析されるので、同じ位置を重ねて報告しない。
        if (!reported.Add($"{location.Span.Start}:{semantic.Name}"))
        {
            return;
        }

        context.ReportDiagnostic(Diagnostic.Create(
            HlslRuleDescriptors.DuplicateSemantic, location, semantic.Name, declaration.Name));
    }
}
