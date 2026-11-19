using System.Collections.Immutable;
using Shaderlyn.CFamily.Syntax;
using Shaderlyn.Core.Analysis;
using Shaderlyn.Core.Diagnostics;
using Shaderlyn.Semantics;
using Shaderlyn.Semantics.Programs;

namespace Shaderlyn.Rules.Wgsl;

/// <summary>書いた属性 1 つ (<c>@location(0)</c> の名前と、括弧の中のトークン)。</summary>
internal readonly record struct WgslAttribute(ShaderSyntaxToken Name, ImmutableArray<ShaderSyntaxToken> Arguments)
{
    /// <summary>括弧の中が 1 つの名前か数なら、その字面。</summary>
    public string? Value => Arguments is [{ } only] ? only.Text : null;

    /// <summary>修飾子のトークンに並んだ属性を読む (<c>@ location ( 0 )</c>)。</summary>
    public static ImmutableArray<WgslAttribute> Read(ImmutableArray<ShaderSyntaxToken> tokens)
    {
        ImmutableArray<WgslAttribute>.Builder attributes = ImmutableArray.CreateBuilder<WgslAttribute>();

        for (int i = 0; i + 1 < tokens.Length; i++)
        {
            if (tokens[i] is not { Kind: ShaderSyntaxKind.BadToken, Text: "@" } || tokens[i + 1].Kind != ShaderSyntaxKind.IdentifierToken)
            {
                continue;
            }

            ShaderSyntaxToken name = tokens[i + 1];
            ImmutableArray<ShaderSyntaxToken> arguments = [];

            if (i + 2 < tokens.Length && tokens[i + 2].Kind == ShaderSyntaxKind.OpenParenToken)
            {
                int depth = 0;
                int end = i + 2;

                for (; end < tokens.Length; end++)
                {
                    depth += tokens[end].Kind switch
                    {
                        ShaderSyntaxKind.OpenParenToken => 1,
                        ShaderSyntaxKind.CloseParenToken => -1,
                        _ => 0,
                    };

                    if (depth == 0)
                    {
                        break;
                    }
                }

                arguments = [.. tokens.Skip(i + 3).Take(end - i - 3).Where(t => t.Kind != ShaderSyntaxKind.CommaToken || true)];

                // 最後の , (@location(0,)) は外す。
                if (arguments is [.. var head, { Kind: ShaderSyntaxKind.CommaToken }])
                {
                    arguments = [.. head];
                }
            }

            attributes.Add(new WgslAttribute(name, arguments));
        }

        return attributes.ToImmutable();
    }

    /// <summary>属性のノードから読む (関数の属性と戻り値の属性)。</summary>
    public static WgslAttribute From(AttributeSyntax attribute) => new(attribute.NameToken, attribute.ArgumentTokens);

    /// <summary>戻り値の属性 (関数の後ろの修飾にしてある) から読む。</summary>
    public static WgslAttribute From(SemanticSyntax semantic) => new(semantic.NameToken, semantic.ArgumentTokens);
}

/// <summary>
/// WGSL のエントリポイント (WG0322) と、ステージで使えない操作 (WG0002) を検査する。
/// </summary>
/// <remarks>
/// <para>
/// WG0322: エントリポイントの仮引数と戻り値 (構造体ならそのメンバー) には <c>@location</c> か <c>@builtin</c> が要る。頂点シェーダーは <c>@builtin(position)</c> を返す。
/// <c>@blend_src</c> は、構造体の location 0 のメンバー 2 つに 0 と 1 を書き (型は同じ、ほかに @location のメンバーは無い)、頂点シェーダーでは使えない。
/// <c>@builtin(clip_distances)</c> は 8 以下の大きさの <c>array&lt;f32, N&gt;</c>。
/// </para>
/// <para>
/// WG0002: 頂点・計算のエントリポイントから呼ぶ関数の、discard・微分 (dpdx など)・暗黙の微分の標本 (textureSample・textureSampleBias・textureSampleCompare)。
/// 頂点・フラグメントのエントリポイントから呼ぶ関数の、同期 (workgroupBarrier・storageBarrier・textureBarrier・subgroupBarrier) と workgroupUniformLoad。
/// </para>
/// naga 30 はどれもコンパイルの誤りにする。
/// </remarks>
internal sealed class WgslEntryPointAnalyzer : SemanticRuleAnalyzer
{
    private static readonly string[] FragmentOnly =
    [
        "dpdx", "dpdy", "fwidth", "dpdxCoarse", "dpdyCoarse", "fwidthCoarse", "dpdxFine", "dpdyFine", "fwidthFine",
        "textureSample", "textureSampleBias", "textureSampleCompare",
    ];

    private static readonly string[] ComputeOnly = ["workgroupBarrier", "storageBarrier", "textureBarrier", "subgroupBarrier", "workgroupUniformLoad"];

    /// <inheritdoc/>
    protected override bool AnalyzesWgsl => true;

    /// <inheritdoc/>
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [WgslRuleDescriptors.InvalidEntryPoint, WgslRuleDescriptors.NotAvailableInStage];

    /// <inheritdoc/>
    protected override void Analyze(SyntaxTreeAnalysisContext context, ShaderCompilation compilation)
    {
        if (!compilation.IsWgsl)
        {
            return;
        }

        HashSet<(string? File, int Start, string Id)> reported = [];

        void Report(DiagnosticDescriptor descriptor, ShaderSyntaxToken? token, string message)
        {
            if (token is { IsMissing: false }
                && compilation.IsReportable(token)
                && token.GetLocation() is { } location
                && reported.Add((location.FilePath, location.Span.Start, descriptor.Id)))
            {
                context.ReportDiagnostic(Diagnostic.Create(descriptor, location, message));
            }
        }

        foreach (AnalyzedProgram program in compilation.Programs)
        {
            CompilationUnitSyntax root = program.Tree.Root;
            Dictionary<string, StructDeclarationSyntax> structs = new(StringComparer.Ordinal);

            foreach (StructDeclarationSyntax structure in root.Declarations.OfType<StructDeclarationSyntax>())
            {
                if (structure.NameToken is { } name)
                {
                    structs.TryAdd(name.Text, structure);
                }

                CheckBlendSource(structure, Report);
                CheckClipDistances(structure, Report);
            }

            Dictionary<string, FunctionDeclarationSyntax> functions = new(StringComparer.Ordinal);

            foreach (FunctionDeclarationSyntax function in root.Declarations.OfType<FunctionDeclarationSyntax>())
            {
                functions.TryAdd(function.Name, function);
            }

            foreach (FunctionDeclarationSyntax function in functions.Values)
            {
                if (StageOf(function) is not { } stage)
                {
                    continue;
                }

                CheckInterface(structs, function, stage, Report);
                CheckStageOperations(functions, function, stage, Report);
            }
        }
    }

    /// <summary>エントリポイントのステージ (<c>vertex</c> / <c>fragment</c> / <c>compute</c>)。</summary>
    private static string? StageOf(FunctionDeclarationSyntax function)
        => function.Attributes.Select(a => a.NameToken.Text).FirstOrDefault(n => n is "vertex" or "fragment" or "compute");

    /// <summary>仮引数と戻り値の、@location / @builtin。</summary>
    private static void CheckInterface(
        Dictionary<string, StructDeclarationSyntax> structs,
        FunctionDeclarationSyntax function,
        string stage,
        Action<DiagnosticDescriptor, ShaderSyntaxToken?, string> report)
    {
        bool hasPosition = false;

        foreach (ParameterSyntax parameter in function.ParameterList)
        {
            ImmutableArray<WgslAttribute> attributes = WgslAttribute.Read(parameter.ModifierTokens);

            if (attributes.FirstOrDefault(a => a.Name.Text == "blend_src") is { Name: { } blend })
            {
                report(WgslRuleDescriptors.InvalidEntryPoint, blend, "@blend_src はエントリポイントの入出力に直接は書けません。構造体のメンバーに書いてください。");
            }

            CheckBinding(structs, attributes, parameter.Type, parameter.NameToken, $"仮引数 '{parameter.Name}'", stage, report, ref hasPosition);
        }

        // 戻り値 (-> の後ろの属性は、関数の後ろの修飾にしてある)。
        if (function.ReturnType.Name != "void")
        {
            ImmutableArray<WgslAttribute> attributes = [.. function.Semantics.Select(WgslAttribute.From)];

            if (attributes.FirstOrDefault(a => a.Name.Text == "blend_src") is { Name: { } blend })
            {
                report(WgslRuleDescriptors.InvalidEntryPoint, blend, "@blend_src はエントリポイントの入出力に直接は書けません。構造体のメンバーに書いてください。");
            }

            hasPosition = false;
            CheckBinding(structs, attributes, function.ReturnType, function.ReturnType.NameToken, "戻り値", stage, report, ref hasPosition);

            if (stage == "vertex" && !hasPosition)
            {
                report(WgslRuleDescriptors.InvalidEntryPoint, function.NameToken, $"頂点シェーダー '{function.Name}' は @builtin(position) を返さなければなりません。");
            }
        }
        else if (stage == "vertex")
        {
            report(WgslRuleDescriptors.InvalidEntryPoint, function.NameToken, $"頂点シェーダー '{function.Name}' は @builtin(position) を返さなければなりません。");
        }
    }

    /// <summary>入出力 1 つの @location / @builtin。構造体ならメンバーごとに見る。</summary>
    private static void CheckBinding(
        Dictionary<string, StructDeclarationSyntax> structs,
        ImmutableArray<WgslAttribute> attributes,
        TypeSyntax type,
        ShaderSyntaxToken at,
        string what,
        string stage,
        Action<DiagnosticDescriptor, ShaderSyntaxToken?, string> report,
        ref bool hasPosition)
    {
        if (attributes.Any(a => a.Name.Text is "location" or "builtin"))
        {
            hasPosition |= attributes.Any(a => a.Name.Text == "builtin" && a.Value == "position");
            return;
        }

        if (!structs.TryGetValue(type.Name, out StructDeclarationSyntax? structure))
        {
            report(WgslRuleDescriptors.InvalidEntryPoint, at, $"エントリポイントの{what}に @location か @builtin がありません。");
            return;
        }

        foreach (VariableDeclarationSyntax member in structure.Members.OfType<VariableDeclarationSyntax>())
        {
            ImmutableArray<WgslAttribute> memberAttributes = WgslAttribute.Read(member.ModifierTokens);
            ShaderSyntaxToken? name = member.Variables.FirstOrDefault()?.NameToken;

            if (!memberAttributes.Any(a => a.Name.Text is "location" or "builtin"))
            {
                report(WgslRuleDescriptors.InvalidEntryPoint, name, $"エントリポイントの{what}の構造体 {structure.Name} のメンバー '{name?.Text}' に @location か @builtin がありません。");
            }

            hasPosition |= memberAttributes.Any(a => a.Name.Text == "builtin" && a.Value == "position");

            if (stage == "vertex" && memberAttributes.FirstOrDefault(a => a.Name.Text == "blend_src") is { Name: { } blend })
            {
                report(WgslRuleDescriptors.InvalidEntryPoint, blend, "頂点シェーダーの入出力に @blend_src は使えません (フラグメントシェーダーの出力だけ)。");
            }
        }
    }

    /// <summary>@blend_src を書いた構造体の決まり。</summary>
    private static void CheckBlendSource(StructDeclarationSyntax structure, Action<DiagnosticDescriptor, ShaderSyntaxToken?, string> report)
    {
        (VariableDeclarationSyntax Member, ImmutableArray<WgslAttribute> Attributes)[] members =
            [.. structure.Members.OfType<VariableDeclarationSyntax>().Select(m => (m, WgslAttribute.Read(m.ModifierTokens)))];
        var sources = members.Where(m => m.Attributes.Any(a => a.Name.Text == "blend_src")).ToArray();

        if (sources.Length == 0)
        {
            return;
        }

        ShaderSyntaxToken at = sources[0].Attributes.First(a => a.Name.Text == "blend_src").Name;

        foreach (var source in sources)
        {
            WgslAttribute blend = source.Attributes.First(a => a.Name.Text == "blend_src");
            string? location = source.Attributes.FirstOrDefault(a => a.Name.Text == "location").Value;

            if (blend.Value is not ("0" or "0u" or "0i" or "1" or "1u" or "1i") || location is not ("0" or "0u" or "0i"))
            {
                report(WgslRuleDescriptors.InvalidEntryPoint, blend.Name, "@blend_src は @location(0) のメンバーに、0 と 1 だけを書けます。");
                return;
            }
        }

        string[] indices = [.. sources.Select(s => s.Attributes.First(a => a.Name.Text == "blend_src").Value!.TrimEnd('u', 'i'))];

        if (indices.Distinct().Count() != indices.Length)
        {
            report(WgslRuleDescriptors.InvalidEntryPoint, at, "同じ @blend_src を 2 度書いています。@blend_src(0) と @blend_src(1) を 1 つずつ書いてください。");
        }
        else if (!(indices.Contains("0") && indices.Contains("1")))
        {
            report(WgslRuleDescriptors.InvalidEntryPoint, at, "@blend_src を書いた構造体には、@blend_src(0) と @blend_src(1) の 2 つの出力が必要です。");
        }
        else if (sources[0].Member.Type.Name != sources[1].Member.Type.Name)
        {
            report(WgslRuleDescriptors.InvalidEntryPoint, at, "@blend_src(0) と @blend_src(1) のメンバーは同じ型でなければなりません。");
        }
        else if (members.FirstOrDefault(m => !m.Attributes.Any(a => a.Name.Text == "blend_src") && m.Attributes.Any(a => a.Name.Text == "location")) is { Member: { } other })
        {
            report(WgslRuleDescriptors.InvalidEntryPoint, other.Variables.FirstOrDefault()?.NameToken,
                "@blend_src を書いた構造体の、ほかのメンバーには @location を書けません。");
        }
    }

    /// <summary>@builtin(clip_distances) の型。</summary>
    private static void CheckClipDistances(StructDeclarationSyntax structure, Action<DiagnosticDescriptor, ShaderSyntaxToken?, string> report)
    {
        foreach (VariableDeclarationSyntax member in structure.Members.OfType<VariableDeclarationSyntax>())
        {
            if (!WgslAttribute.Read(member.ModifierTokens).Any(a => a.Name.Text == "builtin" && a.Value == "clip_distances"))
            {
                continue;
            }

            bool valid = member.Type is { NameToken.Text: "array", TemplateArguments: [{ Text: "f32" }, { Kind: ShaderSyntaxKind.CommaToken }, { Kind: ShaderSyntaxKind.NumericLiteralToken } size] }
                         && WgslConstants.ParseLiteral(size) is { Kind: WgslValueKind.Integer } count
                         && count.Integer >= 1 && count.Integer <= 8;

            if (!valid && member.Type.TemplateArguments is [{ Text: "f32" }, { Kind: ShaderSyntaxKind.CommaToken }, { Kind: ShaderSyntaxKind.NumericLiteralToken }])
            {
                report(WgslRuleDescriptors.InvalidEntryPoint, member.Type.NameToken, "@builtin(clip_distances) の型は array<f32, N> (N は 8 以下) でなければなりません。");
            }
        }
    }

    /// <summary>ステージで使えない操作 (エントリポイントから呼ぶ関数の中も)。</summary>
    private static void CheckStageOperations(
        Dictionary<string, FunctionDeclarationSyntax> functions,
        FunctionDeclarationSyntax entry,
        string stage,
        Action<DiagnosticDescriptor, ShaderSyntaxToken?, string> report)
    {
        HashSet<string> visited = new(StringComparer.Ordinal) { entry.Name };
        Queue<FunctionDeclarationSyntax> queue = new([entry]);

        while (queue.Count > 0)
        {
            FunctionDeclarationSyntax function = queue.Dequeue();

            if (function.Body is null)
            {
                continue;
            }

            foreach (ShaderSyntaxNode node in function.Body.DescendantNodesAndSelf())
            {
                switch (node)
                {
                    case JumpStatementSyntax { Keyword.Text: "discard" } discard when stage != "fragment":
                        report(WgslRuleDescriptors.NotAvailableInStage, discard.Keyword, $"discard はフラグメントシェーダーでだけ使えます ({Describe(stage, entry)})。");
                        break;

                    case InvocationExpressionSyntax { TargetName: { } name } call:
                        if (functions.TryGetValue(name, out FunctionDeclarationSyntax? callee))
                        {
                            if (visited.Add(name))
                            {
                                queue.Enqueue(callee);
                            }
                        }
                        else if (stage != "fragment" && FragmentOnly.Contains(name) && call.Target is IdentifierExpressionSyntax fragment)
                        {
                            report(WgslRuleDescriptors.NotAvailableInStage, fragment.Identifier, $"{name} はフラグメントシェーダーでだけ使えます ({Describe(stage, entry)})。");
                        }
                        else if (stage != "compute" && ComputeOnly.Contains(name) && call.Target is IdentifierExpressionSyntax compute)
                        {
                            report(WgslRuleDescriptors.NotAvailableInStage, compute.Identifier, $"{name} は計算シェーダーでだけ使えます ({Describe(stage, entry)})。");
                        }

                        break;
                }
            }
        }
    }

    private static string Describe(string stage, FunctionDeclarationSyntax entry)
        => $"{stage switch { "vertex" => "頂点シェーダー", "compute" => "計算シェーダー", _ => "フラグメントシェーダー" }} '{entry.Name}' から使っています";
}
