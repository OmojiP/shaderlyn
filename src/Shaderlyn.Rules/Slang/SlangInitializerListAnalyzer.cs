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

            ImmutableArray<ShaderSyntaxToken> tokens = program.Tree.PreprocessResult.Tokens;
            Dictionary<ShaderSyntaxToken, int> index = new(ReferenceEqualityComparer.Instance);

            for (int i = 0; i < tokens.Length; i++)
            {
                index[tokens[i]] = i;
            }

            // 構造体の可視性 (struct の前の public / internal。書かなければ internal)。
            string VisibilityOf(StructDeclarationSyntax structure)
                => index.TryGetValue(structure.Keyword, out int at) && at > 0 && tokens[at - 1].Text is "public" or "internal" or "private" ? tokens[at - 1].Text : "internal";

            // S({ … }) (並びを渡す作成)。
            foreach (InvocationExpressionSyntax call in root.DescendantNodesAndSelf().OfType<InvocationExpressionSyntax>())
            {
                if (call.TargetName is { } constructed
                    && call.ArgumentExpressions.ToArray() is [InitializerListExpressionSyntax argument]
                    && structs.TryGetValue(constructed, out StructDeclarationSyntax? target) && target is not null
                    && target.GenericParameterTokens.IsDefaultOrEmpty
                    && !open.Contains(constructed)
                    && DescribeCall(structs, target, argument.Elements.Select(e => e.Node).OfType<ExpressionSyntax>().Count(), VisibilityOf, since2026, binder) is { } callProblem)
                {
                    Report(argument.OpenBrace, callProblem);
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
                            $"{type.Name} の並びで、ベクトルを含んで成分の足りない形 (成分は {written} 個で、{shape.Columns} 個が必要です) は、言語のバージョン 2026 で取り除かれています。足りない成分を書いてください。"));
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
                        || Describe(structs, structure, count, VisibilityOf, since2026) is not { } problem)
                    {
                        continue;
                    }

                    Report(list.OpenBrace, problem);
                }
            }
        }
    }

    /// <summary>並びが合わない理由。合うか、決められなければ <see langword="null"/>。</summary>
    private static string? Describe(Dictionary<string, StructDeclarationSyntax?> structs, StructDeclarationSyntax structure, int count, Func<StructDeclarationSyntax, string> visibilityOf, bool since2026)
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

        // __init の無い構造体: slangc が作る __init は、構造体と同じかそれより広い可視性のフィールドだけを仮引数にする。
        // 並びの要素がその数を超えれば、合う __init が無い (E30513・E39999)。基底の構造体のフィールドも先に数える。
        if (count > 0 && VisibleFields(structs, structure, visibilityOf, since2026, 0) is { } visible && count > visible.Total && visible.Hidden > 0)
        {
            return $"{name} の初期化子の並びで書けるのは、{name} と同じかそれより広い可視性のフィールドの {visible.Total} 個だけです (private / internal のフィールドは、作られる __init の仮引数になりません)。";
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

    /// <summary>
    /// S({ … }) の並びが合わない理由。__init を書いた構造体では、並びは 1 つの実引数になる。1 つの仮引数の __init が配列などを受け取るなら合いうるので見ず、
    /// どれもスカラーを受け取るなら、要素が 2 つ以上の並びは多すぎる (E30523)。__init の無い構造体は、変数の並びと同じに見る。
    /// </summary>
    private static string? DescribeCall(Dictionary<string, StructDeclarationSyntax?> structs, StructDeclarationSyntax structure, int count, Func<StructDeclarationSyntax, string> visibilityOf, bool since2026, ExpressionTypeBinder binder)
    {
        FunctionDeclarationSyntax[] initializers = [.. structure.Members.OfType<FunctionDeclarationSyntax>().Where(f => f.Name == "__init")];

        if (initializers.Length == 0)
        {
            return Describe(structs, structure, count, visibilityOf, since2026);
        }

        ParameterSyntax[] single = [.. initializers.Where(i => i.ParameterList.Count() == 1).Select(i => i.ParameterList.First())];

        if (single.Length == 0
            || single.Any(p => p.IsArray || !binder.TypeSystem.TryDescribeNumeric(p.Type.Name, out Semantics.Symbols.ShaderNumericShape shape) || shape.Kind != Semantics.Symbols.ShaderNumericKind.Scalar)
            || count <= 1)
        {
            return null;
        }

        return $"{structure.Name} の __init はスカラーを 1 つ受け取るので、要素が {count} 個の並びは渡せません。";
    }

    /// <summary>作られる __init の仮引数になるフィールドの数と、ならないフィールドの数。決められなければ <see langword="null"/>。</summary>
    private static (int Total, int Hidden)? VisibleFields(Dictionary<string, StructDeclarationSyntax?> structs, StructDeclarationSyntax structure, Func<StructDeclarationSyntax, string> visibilityOf, bool since2026, int depth)
    {
        if (depth > 8 || structure.Members.OfType<FunctionDeclarationSyntax>().Any(f => f.Name == "__init"))
        {
            return null;
        }

        int total = 0;
        int hidden = 0;

        if (structure.BaseTypeName is { } baseName)
        {
            if (!structs.TryGetValue(baseName, out StructDeclarationSyntax? baseStructure) || baseStructure is null
                || VisibleFields(structs, baseStructure, visibilityOf, since2026, depth + 1) is not { } inherited)
            {
                return null;
            }

            total += inherited.Total;
            hidden += inherited.Hidden;
        }

        string own = visibilityOf(structure);
        int Rank(string visibility) => visibility switch { "public" => 2, "internal" => 1, _ => 0 };

        foreach (VariableDeclarationSyntax field in structure.Members.OfType<VariableDeclarationSyntax>())
        {
            if (field.ModifierTokens.Any(t => t.Text is "static" or "const"))
            {
                continue;
            }

            // フィールドの可視性の既定は internal。2026 の言語では、public の構造体のフィールドは public。
            string visibility = field.ModifierTokens.FirstOrDefault(t => t.Text is "public" or "internal" or "private")?.Text
                                ?? (since2026 && own == "public" ? "public" : "internal");
            int count = field.Variables.Count();

            if (Rank(visibility) >= Rank(own))
            {
                total += count;
            }
            else
            {
                hidden += count;
            }
        }

        return (total, hidden);
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
