using System.Collections.Immutable;
using Shaderlyn.CFamily.Syntax;
using Shaderlyn.Core.Analysis;
using Shaderlyn.Core.Diagnostics;
using Shaderlyn.Hlsl.Syntax;
using Shaderlyn.Semantics;
using Shaderlyn.Semantics.Programs;

namespace Shaderlyn.Rules.Slang;

/// <summary>
/// Slang の変数の宣言の決まりを検査する (SG0102・SG0103・SG0316・SG0403)。
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>SG0102: 関数の外の static の無い const の初期値つきの変数と constexpr の変数、初期値つきの uniform (slangc の E31224)、
/// 局所変数の groupshared、構造体のフィールドの __ref / __constref (E31201)</item>
/// <item>SG0103: register(…) の種類 (t・u・s・b) が資源の型と合わない (E31222)</item>
/// <item>SG0316: static const の初期値の、uniform・定数バッファーのメンバー・関数の外の変数・利用者の関数とメソッドの呼び出し (E31226)</item>
/// <item>SG0403: ParameterBlock・大きさを決めない配列・それを含む構造体の配列 (E30028)</item>
/// </list>
/// GLSL の書き方のファイル (#version のあるもの) は、GLSL の決まりで読むので見ない。
/// </remarks>
internal sealed class SlangDeclarationAnalyzer : SemanticRuleAnalyzer
{
    /// <summary>t (読むだけ) のレジスタの資源。</summary>
    private static readonly string[] ShaderResources =
    [
        "Texture1D", "Texture1DArray", "Texture2D", "Texture2DArray", "Texture2DMS", "Texture2DMSArray", "Texture3D", "TextureCube", "TextureCubeArray",
        "StructuredBuffer", "ByteAddressBuffer", "Buffer", "RaytracingAccelerationStructure",
    ];

    /// <summary>u (書ける) のレジスタの資源。</summary>
    private static readonly string[] UnorderedResources =
    [
        "RWTexture1D", "RWTexture1DArray", "RWTexture2D", "RWTexture2DArray", "RWTexture2DMS", "RWTexture2DMSArray", "RWTexture3D",
        "RWStructuredBuffer", "RWByteAddressBuffer", "RWBuffer", "AppendStructuredBuffer", "ConsumeStructuredBuffer",
        "RasterizerOrderedTexture1D", "RasterizerOrderedTexture1DArray", "RasterizerOrderedTexture2D", "RasterizerOrderedTexture2DArray",
        "RasterizerOrderedTexture3D", "RasterizerOrderedBuffer", "RasterizerOrderedByteAddressBuffer", "RasterizerOrderedStructuredBuffer",
        "FeedbackTexture2D", "FeedbackTexture2DArray",
    ];

    private static readonly string[] Samplers = ["SamplerState", "SamplerComparisonState"];

    /// <inheritdoc/>
    protected override bool AnalyzesSlang => true;

    /// <inheritdoc/>
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
    [
        SlangRuleDescriptors.ModifierNotAllowed,
        SlangRuleDescriptors.RegisterMismatch,
        SlangRuleDescriptors.StaticConstNotConstant,
        SlangRuleDescriptors.ArrayOfNonAddressable,
        SlangRuleDescriptors.ResourceInContainer,
    ];

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

            if (IsGlslSource(program.Tree))
            {
                continue;
            }

            void Report(DiagnosticDescriptor descriptor, ShaderSyntaxToken? token, string message)
            {
                if (token is { IsMissing: false } && compilation.IsReportable(token) && token.GetLocation() is { } location)
                {
                    context.ReportDiagnostic(Diagnostic.Create(descriptor, location, message));
                }
            }

            Dictionary<string, StructDeclarationSyntax> structs = new(StringComparer.Ordinal);

            foreach (StructDeclarationSyntax structure in root.DescendantNodesAndSelf().OfType<StructDeclarationSyntax>())
            {
                if (structure.NameToken is { } name)
                {
                    structs.TryAdd(name.Text, structure);
                }
            }

            bool host = compilation.Environment is Semantics.Environments.SlangEnvironment { TargetsHost: true };
            HashSet<string> userFunctions = [.. root.DescendantNodesAndSelf().OfType<FunctionDeclarationSyntax>().Select(f => f.Name)];
            ExpressionTypeBinder binder = compilation.GetExpressionTypeBinder(program);

            foreach (ShaderSyntaxNode node in root.DescendantNodesAndSelf())
            {
                switch (node)
                {
                    // StructuredBuffer<Texture2D>・Optional<SamplerState> (E38204・E30902)。CPU と CUDA のコンパイル先 (slang: target) は、資源をバッファーに入れられる。
                    case TypeSyntax type when (type.NameToken.Text is "Optional"
                                               || (!host && type.NameToken.Text is "StructuredBuffer" or "RWStructuredBuffer" or "AppendStructuredBuffer"
                                                   or "ConsumeStructuredBuffer" or "RasterizerOrderedStructuredBuffer"))
                                              && type.TemplateArguments.FirstOrDefault(t => t.Kind == ShaderSyntaxKind.IdentifierToken) is { } element
                                              && !IsHandle(type.TemplateArguments)
                                              && ContainsResource(structs, element.Text, 0):
                        Report(SlangRuleDescriptors.ResourceInContainer, type.NameToken,
                            $"{type.NameToken.Text} に資源の型 ({element.Text}{(IsResource(element.Text) ? "" : " が資源を含む")}) は包めません。");
                        break;

                    // 同じ属性を 2 度 ([numthreads] など)。
                    case FunctionDeclarationSyntax function:
                        CheckAttributesAndVisibility(function.Attributes, function.ModifierTokens, Report);
                        break;
                }
            }

            foreach (VariableDeclarationSyntax declaration in root.DescendantNodesAndSelf().OfType<VariableDeclarationSyntax>())
            {
                bool global = declaration.Parent is CompilationUnitSyntax or NamespaceDeclarationSyntax;
                bool field = declaration.Parent is StructDeclarationSyntax;
                bool local = declaration.Parent is LocalDeclarationStatementSyntax;
                ImmutableArray<ShaderSyntaxToken> modifiers = declaration.ModifierTokens;
                bool isStatic = modifiers.Any(t => t.Text == "static");

                foreach (VariableDeclaratorSyntax declarator in declaration.Variables)
                {
                    // 特殊化定数 ([vk::constant_id(0)] const int N = 1;) は、static の無い const に初期値を書ける。
                    if (global && !IsSpecializationConstant(program.Tree.PreprocessResult.Tokens, declaration))
                    {
                        CheckGlobalModifiers(declaration, declarator, isStatic, Report);
                    }

                    if (global && isStatic && modifiers.Any(t => t.Text == "const") && declarator.Initializer is { } initializer
                        && FindNonConstant(binder, userFunctions, program.Tree.PreprocessResult.Tokens, initializer) is { } what)
                    {
                        Report(SlangRuleDescriptors.StaticConstNotConstant, declarator.NameToken,
                            $"static const の '{declarator.Name}' の初期値には定数式が要りますが、{what}を使っています。");
                    }

                    if (global)
                    {
                        CheckRegister(declaration, declarator, Report);
                    }

                    if (declarator.IsArray && (global || field || local) && NonAddressable(structs, declaration.Type, declarator, 0) is { } reason)
                    {
                        Report(SlangRuleDescriptors.ArrayOfNonAddressable, declarator.NameToken, $"'{declarator.Name}' は配列にできません ({reason})。");
                    }
                }

                if (local && !isStatic && modifiers.FirstOrDefault(t => t.Text == "groupshared") is { } groupshared)
                {
                    Report(SlangRuleDescriptors.ModifierNotAllowed, groupshared, "局所変数に groupshared は書けません。関数の外に宣言してください。");
                }

                if (field && modifiers.FirstOrDefault(t => t.Text is "__ref" or "__constref") is { } reference)
                {
                    Report(SlangRuleDescriptors.ModifierNotAllowed, reference, $"構造体のフィールドに {reference.Text} は書けません。");
                }
            }
        }
    }

    /// <summary>資源の型 (テクスチャ・サンプラー・バッファー・ParameterBlock・ConstantBuffer)。</summary>
    private static bool IsResource(string name)
        => ShaderResources.Contains(name) || UnorderedResources.Contains(name) || Samplers.Contains(name)
           || name is "ParameterBlock" or "ConstantBuffer" or "TextureBuffer" || name.StartsWith("Sampler", StringComparison.Ordinal) && name.Length > 7 && char.IsDigit(name[7]);

    /// <summary>資源の型か、資源をメンバーに持つ構造体か。</summary>
    private static bool ContainsResource(Dictionary<string, StructDeclarationSyntax> structs, string name, int depth)
        => IsResource(name)
           || (depth < 16 && structs.TryGetValue(name, out StructDeclarationSyntax? structure)
               && structure.Members.OfType<VariableDeclarationSyntax>()
                   .Where(m => !m.ModifierTokens.Any(t => t.Text == "static"))
                   .Any(m => m.Type.NestedTypeTokens.IsDefaultOrEmpty && ContainsResource(structs, m.Type.NameToken.Text, depth + 1)));

    /// <summary>資源への参照 (Texture2D.Handle・DescriptorHandle&lt;T&gt;) か。資源そのものではないので包める。</summary>
    private static bool IsHandle(ImmutableArray<ShaderSyntaxToken> arguments)
        => arguments.Any(t => t.Text is "Handle" or "DescriptorHandle");

    /// <summary>1 つしか書けない属性の繰り返しと、可視性の重ね書き (E31202)。</summary>
    private static void CheckAttributesAndVisibility(
        ImmutableArray<AttributeSyntax> attributes,
        ImmutableArray<ShaderSyntaxToken> modifiers,
        Action<DiagnosticDescriptor, ShaderSyntaxToken?, string> report)
    {
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);

        foreach (AttributeSyntax attribute in attributes)
        {
            if (SingletonAttributes.Contains(attribute.NameToken.Text) && !seen.Add(attribute.NameToken.Text))
            {
                report(SlangRuleDescriptors.ModifierNotAllowed, attribute.NameToken, $"[{attribute.NameToken.Text}] は 1 つしか書けません。");
            }
        }

        ShaderSyntaxToken[] visibility = [.. modifiers.Where(t => t.Text is "public" or "internal" or "private")];

        if (visibility.Length > 1)
        {
            report(SlangRuleDescriptors.ModifierNotAllowed, visibility[1], $"可視性は 1 つしか書けません ({string.Join("・", visibility.Select(v => v.Text))})。");
        }
    }

    /// <summary>1 つしか書けない属性。</summary>
    private static readonly string[] SingletonAttributes =
    [
        "numthreads", "shader", "NodeLaunch", "NodeMaxDispatchGrid", "NodeDispatchGrid", "NodeIsProgramEntry", "NodeID", "NodeShareInputOf",
        "NodeMaxRecursionDepth", "NodeLocalRootArgumentsTableIndex", "WaveSize", "outputtopology", "domain", "partitioning", "maxvertexcount",
        "instance", "outputcontrolpoints", "patchconstantfunc",
    ];

    /// <summary>関数の外の const / constexpr / uniform の決まり (E31224)。</summary>
    private static void CheckGlobalModifiers(
        VariableDeclarationSyntax declaration,
        VariableDeclaratorSyntax declarator,
        bool isStatic,
        Action<DiagnosticDescriptor, ShaderSyntaxToken?, string> report)
    {
        ImmutableArray<ShaderSyntaxToken> modifiers = declaration.ModifierTokens;

        if (!isStatic && modifiers.FirstOrDefault(t => t.Text == "constexpr") is { } constexpr)
        {
            report(SlangRuleDescriptors.ModifierNotAllowed, constexpr, $"関数の外の constexpr の '{declarator.Name}' には static が要ります (static constexpr と書きます)。");
        }
        else if (!isStatic && declarator.Initializer is not null && modifiers.FirstOrDefault(t => t.Text == "const") is { } constant)
        {
            report(SlangRuleDescriptors.ModifierNotAllowed, constant, $"関数の外の const の '{declarator.Name}' には static が要ります (static const と書きます)。static の無い const は uniform になり、初期値を持てません。");
        }
        else if (declarator.Initializer is not null && modifiers.FirstOrDefault(t => t.Text == "uniform") is { } uniform)
        {
            report(SlangRuleDescriptors.ModifierNotAllowed, uniform, $"uniform の '{declarator.Name}' に初期値は書けません。値はアプリケーションから入ります。");
        }
    }

    /// <summary>初期値の中の、定数でない値。無ければ <see langword="null"/>。</summary>
    private static string? FindNonConstant(ExpressionTypeBinder binder, HashSet<string> userFunctions, ImmutableArray<ShaderSyntaxToken> tokens, ExpressionSyntax initializer)
    {
        foreach (ShaderSyntaxNode node in initializer.DescendantNodesAndSelf())
        {
            switch (node)
            {
                // 型の static のメソッド (float.getPi()) は定数を返す。値のメソッド (h.get()) は定数でない。
                case InvocationExpressionSyntax { Target: MemberAccessExpressionSyntax member }
                    when !(member.Target is IdentifierExpressionSyntax receiver && binder.TypeSystem.IsBuiltInTypeName(receiver.Name)):
                    return $"メソッド '{member.Name}' の呼び出し";

                case InvocationExpressionSyntax { TargetName: { } name } when userFunctions.Contains(name):
                    return $"関数 '{name}' の呼び出し";

                case IdentifierExpressionSyntax identifier
                    when !(identifier.Parent is InvocationExpressionSyntax call && ReferenceEquals(call.Target, identifier))
                         && binder.ResolveName(identifier).Candidates is [{ Declarator: VariableDeclaratorSyntax { Parent: VariableDeclarationSyntax variable } }]
                         && (variable.Parent is ConstantBufferDeclarationSyntax
                             || (variable.Parent is CompilationUnitSyntax or NamespaceDeclarationSyntax
                                 && !(variable.ModifierTokens.Any(t => t.Text == "static") && variable.ModifierTokens.Any(t => t.Text is "const" or "constexpr"))
                                 && !IsSpecializationConstant(tokens, variable))):
                    return $"定数でない '{identifier.Name}' の値";
            }
        }

        return null;
    }

    /// <summary>register(…) の種類と資源の型 (E31222)。</summary>
    private static void CheckRegister(VariableDeclarationSyntax declaration, VariableDeclaratorSyntax declarator, Action<DiagnosticDescriptor, ShaderSyntaxToken?, string> report)
    {
        string type = declaration.Type.NameToken.Text;
        char? expected = ShaderResources.Contains(type) ? 't'
            : UnorderedResources.Contains(type) ? 'u'
            : Samplers.Contains(type) ? 's'
            : type == "ConstantBuffer" ? 'b'
            : null;

        if (expected is not { } kind)
        {
            return;
        }

        foreach (SemanticSyntax semantic in declarator.Semantics)
        {
            if (semantic.NameToken.Text.Equals("register", StringComparison.OrdinalIgnoreCase)
                && semantic.ArgumentTokens.FirstOrDefault() is { Kind: ShaderSyntaxKind.IdentifierToken } slot
                && slot.Text.Length >= 1 && char.IsAsciiLetter(slot.Text[0])
                && char.ToLowerInvariant(slot.Text[0]) is var given && given != kind
                && given is 't' or 'u' or 's' or 'b')
            {
                report(SlangRuleDescriptors.RegisterMismatch, slot, $"{type} のレジスタは {kind} ですが、{slot.Text} を書いています。");
            }
        }
    }

    /// <summary>配列にできない理由 (ParameterBlock、大きさを決めない配列、それを含む構造体)。配列にできれば <see langword="null"/>。</summary>
    private static string? NonAddressable(Dictionary<string, StructDeclarationSyntax> structs, TypeSyntax type, VariableDeclaratorSyntax? declarator, int depth)
    {
        if (depth > 16)
        {
            return null;
        }

        if (type.NameToken.Text == "ParameterBlock")
        {
            return "ParameterBlock はアドレスを持たない";
        }

        // int a[3][] (大きさを決めない配列の配列)。最初の [] の後ろに、大きさを決めない [] がある。
        if (declarator is not null && UnboundedInnerRank(declarator.ArrayRankTokens))
        {
            return "大きさを決めない配列の配列になる";
        }

        if (!structs.TryGetValue(type.Name, out StructDeclarationSyntax? structure))
        {
            return null;
        }

        foreach (VariableDeclarationSyntax member in structure.Members.OfType<VariableDeclarationSyntax>())
        {
            foreach (VariableDeclaratorSyntax field in member.Variables)
            {
                if (member.Type.NameToken.Text == "ParameterBlock" || IsUnbounded(field.ArrayRankTokens))
                {
                    return $"構造体 {structure.Name} が ParameterBlock か大きさを決めない配列を含む";
                }

                if (NonAddressable(structs, member.Type, null, depth + 1) is not null)
                {
                    return $"構造体 {structure.Name} が配列にできない型を含む";
                }
            }
        }

        return null;
    }

    /// <summary>どこかの次元が大きさを決めない ([]) か。</summary>
    private static bool IsUnbounded(ImmutableArray<ShaderSyntaxToken> ranks)
        => ranks.Select((t, i) => (t, i)).Any(p => p.t.Kind == ShaderSyntaxKind.OpenBracketToken && p.i + 1 < ranks.Length && ranks[p.i + 1].Kind == ShaderSyntaxKind.CloseBracketToken);

    /// <summary>2 つ目より後ろの次元が大きさを決めない ([]) か。</summary>
    private static bool UnboundedInnerRank(ImmutableArray<ShaderSyntaxToken> ranks)
    {
        int rank = 0;

        for (int i = 0; i < ranks.Length; i++)
        {
            if (ranks[i].Kind != ShaderSyntaxKind.OpenBracketToken)
            {
                continue;
            }

            if (rank > 0 && i + 1 < ranks.Length && ranks[i + 1].Kind == ShaderSyntaxKind.CloseBracketToken)
            {
                return true;
            }

            rank++;
        }

        return false;
    }

    /// <summary>宣言の前に、特殊化定数の属性 (vk::constant_id・vk::specialization_constant・SpecializationConstant) があるか。</summary>
    private static bool IsSpecializationConstant(ImmutableArray<ShaderSyntaxToken> tokens, VariableDeclarationSyntax declaration)
    {
        // layout(constant_id = 0) const int N = 1; の layout は宣言の修飾子に入る。
        if (declaration.ModifierTokens.Any(t => t.Text is "constant_id" or "specialization_constant"))
        {
            return true;
        }

        int index = declaration.FirstToken is { } first ? tokens.IndexOf(first) : -1;

        if (index < 0)
        {
            return false;
        }

        for (int i = index - 1; i >= 0 && tokens[i].Kind is not (ShaderSyntaxKind.SemicolonToken or ShaderSyntaxKind.CloseBraceToken or ShaderSyntaxKind.OpenBraceToken); i--)
        {
            if (tokens[i].Text is "constant_id" or "specialization_constant" or "SpecializationConstant")
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>GLSL の書き方のファイルか (#version がある)。</summary>
    private static bool IsGlslSource(CFamily.Syntax.ShaderSyntaxTree tree)
        => System.Text.RegularExpressions.Regex.IsMatch(tree.Text.ToString(), @"^\s*#\s*version\b", System.Text.RegularExpressions.RegexOptions.Multiline);
}
