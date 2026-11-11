using System.Collections.Immutable;
using Shaderlyn.CFamily.Syntax;
using Shaderlyn.Core.Analysis;
using Shaderlyn.Core.Diagnostics;
using Shaderlyn.Semantics;
using Shaderlyn.Semantics.Programs;

namespace Shaderlyn.Rules.Slang;

/// <summary>
/// Slang の初期化子の並び (<c>S s = { … };</c>) が、その型に合うかを検査する (SG0351)。
/// </summary>
/// <remarks>
/// <para>slangc 2026.19 に読ませて確かめた決まり。</para>
/// <list type="bullet">
/// <item><c>__init</c> を書いた構造体は、並びの数を受け取る <c>__init</c> が要る (並びは <c>__init</c> の呼び出しになる。E39999「not enough / too many arguments」「no overload」)</item>
/// <item><c>__init</c> を持つ構造体を継承した構造体には、並びを使えない (E30513「cannot use initializer list」)</item>
/// <item>資源の型 (テクスチャ・バッファー・サンプラー) のフィールドは、並びで埋めなければならない (既定の値が無い。E39999)</item>
/// <item>資源の型の変数には、並びを使えない (<c>Texture2D t = {};</c>。E30513)</item>
/// </list>
/// 並びの要素の数が多すぎるもの (C の書き方の並び) は、共通の CF0412 が見る。ジェネリックの構造体と、extension で <c>__init</c> が足されうる型は見ない。
/// </remarks>
internal sealed class SlangInitializerListAnalyzer : SemanticRuleAnalyzer
{
    /// <inheritdoc/>
    protected override bool AnalyzesSlang => true;

    /// <inheritdoc/>
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [SlangRuleDescriptors.InitializerListMismatch, SlangRuleDescriptors.RemovedDeclaration];

    /// <inheritdoc/>
    protected override void Analyze(SyntaxTreeAnalysisContext context, ShaderCompilation compilation)
    {
        if (!compilation.IsSlang)
        {
            return;
        }

        foreach (AnalyzedProgram program in compilation.Programs)
        {
            CompilationUnitSyntax root = program.Tree.Root;
            Dictionary<string, StructDeclarationSyntax?> structs = new(StringComparer.Ordinal);

            foreach (StructDeclarationSyntax structure in root.DescendantNodesAndSelf().OfType<StructDeclarationSyntax>())
            {
                if (structure.NameToken is { } name && structure.Keyword.Text is "struct")
                {
                    structs[name.Text] = structs.ContainsKey(name.Text) ? null : structure;
                }
            }

            SlangOpenTypes open = SlangOpenTypes.Of(compilation);
            ExpressionTypeBinder binder = compilation.GetExpressionTypeBinder(program);
            bool since2026 = DeclarationSyntaxAnalyzer.IsSlang2026(root);

            void Report(ShaderSyntaxToken at, string message)
            {
                if (!at.IsMissing && compilation.IsReportable(at) && at.GetLocation() is { } location)
                {
                    context.ReportDiagnostic(Diagnostic.Create(SlangRuleDescriptors.InitializerListMismatch, location, message));
                }
            }

            foreach (VariableDeclarationSyntax declaration in root.DescendantNodesAndSelf().OfType<VariableDeclarationSyntax>())
            {
                TypeSyntax type = declaration.Type;

                if (type.IsPointer || !type.NestedTypeTokens.IsDefaultOrEmpty)
                {
                    continue;
                }

                foreach (VariableDeclaratorSyntax variable in declaration.Variables)
                {
                    if (variable.IsArray || variable.Initializer is not InitializerListExpressionSyntax list)
                    {
                        continue;
                    }

                    int count = list.Elements.Select(e => e.Node).OfType<ExpressionSyntax>().Count();

                    // 2026 の言語では、ベクトルを含んで成分の足りないベクトルの並び ({ int2(1, 2), 3 }) は取り除かれている (E31207。2025 は非推奨の警告)。
                    if (since2026
                        && binder.TypeSystem.TryDescribeNumeric(type.Name, out Semantics.Symbols.ShaderNumericShape shape)
                        && shape.Kind == Semantics.Symbols.ShaderNumericKind.Vector
                        && CountComponents(binder, list) is { } written && written < shape.Columns
                        && list.Elements.Select(e => e.Node).OfType<ExpressionSyntax>().Any(e => ComponentsOf(binder, e) is > 1)
                        && !list.OpenBrace.IsMissing && compilation.IsReportable(list.OpenBrace) && list.OpenBrace.GetLocation() is { } removedLocation)
                    {
                        context.ReportDiagnostic(Diagnostic.Create(SlangRuleDescriptors.RemovedDeclaration, removedLocation,
                            $"{type.Name} の並びで、ベクトルを含んで成分の足りない形 (成分は {written} 個で、{shape.Columns} 個が要ります) は、言語のバージョン 2026 で取り除かれています。足りない成分を書いてください。"));
                        continue;
                    }

                    if (IsResource(type.Name))
                    {
                        Report(list.OpenBrace, $"資源の型 {type.Name} の変数には、初期化子の並びを使えません。");
                        continue;
                    }

                    if (type.OpenAngle is not null
                        || !structs.TryGetValue(type.Name, out StructDeclarationSyntax? structure) || structure is null
                        || !structure.GenericParameterTokens.IsDefaultOrEmpty
                        || open.Contains(type.Name)
                        || Describe(structs, structure, count) is not { } problem)
                    {
                        continue;
                    }

                    Report(list.OpenBrace, problem);
                }
            }
        }
    }

    /// <summary>並びが合わない理由。合うか、決められなければ <see langword="null"/>。</summary>
    private static string? Describe(Dictionary<string, StructDeclarationSyntax?> structs, StructDeclarationSyntax structure, int count)
    {
        string name = structure.Name;
        FunctionDeclarationSyntax[] initializers = [.. structure.Members.OfType<FunctionDeclarationSyntax>().Where(f => f.Name == "__init")];

        // 継承した基底が __init を持つ (基底の基底はたどらない)。
        if (structure.BaseTypeName is { } baseName && structs.TryGetValue(baseName, out StructDeclarationSyntax? baseStructure)
            && baseStructure is not null && baseStructure.Members.OfType<FunctionDeclarationSyntax>().Any(f => f.Name == "__init"))
        {
            return $"{name} は __init を持つ構造体 {baseName} を継承しているので、初期化子の並びを使えません。__init を書いて呼んでください。";
        }

        if (initializers.Length > 0)
        {
            bool Accepts(FunctionDeclarationSyntax init)
            {
                ParameterSyntax[] parameters = [.. init.ParameterList];
                int required = parameters.Count(p => p.DefaultValue is null);
                return count >= required && count <= parameters.Length;
            }

            if (initializers.Any(i => i.ParameterList.Any(p => p.ModifierTokens.Any(m => m.Text is "expand" or "each"))) || initializers.Any(Accepts))
            {
                return null;
            }

            string accepted = string.Join("・", initializers.Select(i => i.ParameterList.Count()).Distinct().Order());
            return $"{name} の初期化子の並びは __init の呼び出しになりますが、要素の数 ({count}) を受け取る __init がありません (仮引数の数は {accepted})。";
        }

        // __init の無い構造体: 資源の型のフィールドは、並びで埋めなければならない (既定の値が無い)。
        if (structure.BaseTypeName is not null)
        {
            return null;
        }

        int index = 0;

        foreach (VariableDeclarationSyntax field in structure.Members.OfType<VariableDeclarationSyntax>())
        {
            if (field.ModifierTokens.Any(t => t.Text is "static" or "const"))
            {
                continue;
            }

            foreach (VariableDeclaratorSyntax variable in field.Variables)
            {
                if (IsResource(field.Type.Name) && !variable.IsArray && variable.Initializer is null && index >= count)
                {
                    return $"{name} の資源の型のフィールド '{variable.Name}' ({field.Type.Name}) には既定の値が無いので、初期化子の並びで埋めてください。";
                }

                index++;
            }
        }

        return null;
    }

    /// <summary>並びの要素の成分の数の合計。分からない要素があれば <see langword="null"/>。</summary>
    private static int? CountComponents(ExpressionTypeBinder binder, InitializerListExpressionSyntax list)
    {
        int total = 0;

        foreach (ExpressionSyntax element in list.Elements.Select(e => e.Node).OfType<ExpressionSyntax>())
        {
            if (ComponentsOf(binder, element) is not { } count)
            {
                return null;
            }

            total += count;
        }

        return total;
    }

    /// <summary>式の成分の数 (スカラーは 1、ベクトルは長さ)。分からないか、数の型でなければ <see langword="null"/>。</summary>
    private static int? ComponentsOf(ExpressionTypeBinder binder, ExpressionSyntax expression)
        => expression is LiteralExpressionSyntax ? 1
            : binder.GetEvaluatorFor(expression).Evaluate(expression) is { } type && binder.TypeSystem.TryDescribeNumeric(type, out Semantics.Symbols.ShaderNumericShape shape)
                ? shape.Rows * shape.Columns
                : null;

    /// <summary>既定の値の無い資源の型 (テクスチャ・バッファー・サンプラー)。</summary>
    private static bool IsResource(string name)
        => name.StartsWith("Texture", StringComparison.Ordinal) || name.StartsWith("RWTexture", StringComparison.Ordinal)
           || name is "SamplerState" or "SamplerComparisonState" or "ByteAddressBuffer" or "RWByteAddressBuffer"
           || name.EndsWith("StructuredBuffer", StringComparison.Ordinal) || name is "Buffer" or "RWBuffer" or "RaytracingAccelerationStructure";
}
