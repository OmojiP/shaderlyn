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
/// <item>SG0405: テクスチャと型付きバッファーの要素の、行列・構造体・bool・資源の型 (E38029)</item>
/// <item>SG0406: 構造体のフィールドの、参照の型 (Ref&lt;T&gt; など。E30030)</item>
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
        SlangRuleDescriptors.InvalidTexelType,
        SlangRuleDescriptors.ReferenceField,
        SlangRuleDescriptors.DeclarationNotAllowed,
        HlslRuleDescriptors.Redeclaration,
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

            void Report(DiagnosticDescriptor descriptor, ShaderSyntaxToken? token, string message) => ReportFix(descriptor, token, message, null);

            void ReportFix(DiagnosticDescriptor descriptor, ShaderSyntaxToken? token, string message, TokenFix? fix)
            {
                if (token is { IsMissing: false } && compilation.IsReportable(token) && token.GetLocation() is { } location)
                {
                    context.ReportDiagnostic(Diagnostic.Create(descriptor, location, message).WithFix(fix));
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

            bool host = compilation.Environment is Semantics.Environments.SlangEnvironment { AllowsResourcesInBuffers: true };
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

                    // Texture2D<float4x4>・RWBuffer<Sampler1D>・Texture2D<S> (E38029)。テクスチャと型付きバッファーの要素は ITexelElement (bool でないスカラーとベクトル) に限る。
                    case TypeSyntax type when IsTexelContainer(type.NameToken.Text)
                                              && type.TemplateArguments is [{ Kind: ShaderSyntaxKind.IdentifierToken } texel, ..] arguments
                                              && (arguments.Length == 1 || arguments[1].Kind == ShaderSyntaxKind.CommaToken)
                                              && DescribeNonTexel(compilation, program, structs, texel.Text) is { } what:
                        Report(SlangRuleDescriptors.InvalidTexelType, texel,
                            $"{type.NameToken.Text} の要素にできるのは、bool でないスカラーとベクトルだけです ({texel.Text} は{what})。");
                        break;

                    // 同じ属性を 2 度 ([numthreads] など)。
                    case FunctionDeclarationSyntax function:
                        CheckAttributesAndVisibility(function.Attributes, function.ModifierTokens, Report);

                        // 型の中の演算子 (E30073)。演算子は関数の外に書く。呼び出しの operator() だけは型の中に書ける。
                        if (!function.OperatorTokens.IsDefaultOrEmpty
                            && function.Parent is StructDeclarationSyntax owner
                            && function.OperatorTokens is not [{ Kind: ShaderSyntaxKind.OpenParenToken }, { Kind: ShaderSyntaxKind.CloseParenToken }])
                        {
                            Report(SlangRuleDescriptors.DeclarationNotAllowed, function.NameToken,
                                $"演算子 operator{string.Concat(function.OperatorTokens.Select(t => t.Text))} は、型 ({owner.Name}) の中に書けません。関数の外に、仮引数を 2 つ (単項なら 1 つ) にして書いてください。");
                        }

                        break;
                }
            }

            // 構造体のフィールドの重複と、仮引数の重複 (E30200・「ambiguous reference」)。HLSL の決まりの CF0314 は、どちらも見ていない。
            // 条件付きの宣言 (#if の分岐) は、別の構成のものでありうるので、どの構成にもある宣言だけを比べる。
            Semantics.Conditional.ConditionMap conditions = compilation.GetConditionMap();

            void ReportDuplicates(IEnumerable<ShaderSyntaxToken> names)
            {
                Dictionary<string, ShaderSyntaxToken> first = new(StringComparer.Ordinal);

                foreach (ShaderSyntaxToken name in names)
                {
                    if (name.IsMissing || name.Text.Length == 0)
                    {
                        continue;
                    }

                    if (first.TryGetValue(name.Text, out ShaderSyntaxToken? earlier))
                    {
                        if (compilation.IsReportable(name) && name.GetLocation() is { } location && earlier.GetLocation() is { } earlierLocation)
                        {
                            context.ReportDiagnostic(Diagnostic.Create(HlslRuleDescriptors.Redeclaration, location, name.Text,
                                (earlierLocation.LineSpan.Start.Line + 1).ToString(System.Globalization.CultureInfo.InvariantCulture)));
                        }
                    }
                    else
                    {
                        first[name.Text] = name;
                    }
                }
            }

            foreach (StructDeclarationSyntax structure in structs.Values)
            {
                ReportDuplicates(structure.Members.OfType<VariableDeclarationSyntax>()
                    .Where(conditions.IsAlwaysPresent)
                    .SelectMany(field => field.Variables.Select(v => v.NameToken)));
            }

            foreach (FunctionDeclarationSyntax function in root.DescendantNodesAndSelf().OfType<FunctionDeclarationSyntax>())
            {
                ReportDuplicates(function.ParameterList.Where(conditions.IsAlwaysPresent).Select(p => p.NameToken));

                // 重複した仮引数の名前の使用は、どちらを指すか決まらない (E39999「ambiguous reference」)。局所変数で隠したものは見ない。
                ParameterSyntax[] parameters = [.. function.ParameterList.Where(conditions.IsAlwaysPresent)];
                string[] doubled = [.. parameters.GroupBy(p => p.Name, StringComparer.Ordinal).Where(g => g.Key.Length > 0 && g.Count() > 1).Select(g => g.Key)];

                if (doubled.Length > 0 && function.Body is { } body)
                {
                    HashSet<string> locals = [.. body.DescendantNodesAndSelf().OfType<VariableDeclaratorSyntax>().Select(v => v.Name)];

                    foreach (IdentifierExpressionSyntax use in body.DescendantNodesAndSelf().OfType<IdentifierExpressionSyntax>())
                    {
                        if (doubled.Contains(use.Name) && !locals.Contains(use.Name)
                            && !(use.Parent is MemberAccessExpressionSyntax access && !ReferenceEquals(access.Target, use))
                            && compilation.IsReportable(use.Identifier) && use.Identifier.GetLocation() is { } useLocation
                            && parameters.Last(p => p.Name == use.Name).NameToken.GetLocation() is { } declared)
                        {
                            context.ReportDiagnostic(Diagnostic.Create(HlslRuleDescriptors.Redeclaration, useLocation, use.Name,
                                (declared.LineSpan.Start.Line + 1).ToString(System.Globalization.CultureInfo.InvariantCulture)));
                        }
                    }
                }
            }

            // 列挙子の重複 (E30200)。
            foreach (EnumDeclarationSyntax enumeration in root.DescendantNodesAndSelf().OfType<EnumDeclarationSyntax>())
            {
                ReportDuplicates(enumeration.Members.Where(conditions.IsAlwaysPresent).Select(m => m.NameToken));

                // 重複した列挙子の使用 (E::A。E39999「ambiguous reference」)。
                EnumMemberSyntax[] members = [.. enumeration.Members.Where(conditions.IsAlwaysPresent)];
                HashSet<string> doubledMembers = [.. members.GroupBy(m => m.Name, StringComparer.Ordinal).Where(g => g.Count() > 1).Select(g => g.Key)];

                if (doubledMembers.Count > 0 && enumeration.Name.Length > 0)
                {
                    foreach (MemberAccessExpressionSyntax use in root.DescendantNodesAndSelf().OfType<MemberAccessExpressionSyntax>())
                    {
                        if (use.Target is IdentifierExpressionSyntax { Name: var owner } && owner == enumeration.Name && doubledMembers.Contains(use.NameToken.Text)
                            && compilation.IsReportable(use.NameToken) && use.NameToken.GetLocation() is { } memberLocation
                            && members.Last(m => m.Name == use.NameToken.Text).NameToken.GetLocation() is { } memberDeclared)
                        {
                            context.ReportDiagnostic(Diagnostic.Create(HlslRuleDescriptors.Redeclaration, memberLocation, use.NameToken.Text,
                                (memberDeclared.LineSpan.Start.Line + 1).ToString(System.Globalization.CultureInfo.InvariantCulture)));
                        }
                    }
                }
            }

            // 同じ形の関数の本体の 2 つ目 (E30201「function already has a body」)。ジェネリック・where・コンパイル先の指定のある関数は、制約と先で別のものになりうるので見ない。
            ReportRedefinitions(context, compilation, root, conditions);

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
                        CheckGlobalModifiers(declaration, declarator, isStatic, ReportFix);
                    }

                    if (global && isStatic && modifiers.Any(t => t.Text == "const") && declarator.Initializer is { } initializer
                        && FindNonConstant(binder, userFunctions, program.Tree.PreprocessResult.Tokens, initializer) is { } what)
                    {
                        Report(SlangRuleDescriptors.StaticConstNotConstant, declarator.NameToken,
                            $"static const の '{declarator.Name}' の初期値には定数式が必要ですが、{what}を使っています。");
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

                // 参照の型 (Ref<T>・RefParam<T>・BorrowInParam<T>・OutParam<T>・BorrowInOutParam<T>) は、構造体のフィールドにできない (E30030)。
                if (field && !isStatic && declaration.Type.NameToken.Text is "Ref" or "RefParam" or "BorrowInParam" or "OutParam" or "BorrowInOutParam"
                    && !declaration.Type.TemplateArguments.IsDefaultOrEmpty
                    && !structs.ContainsKey(declaration.Type.NameToken.Text))
                {
                    Report(SlangRuleDescriptors.ReferenceField, declaration.Type.NameToken,
                        $"参照の型 {declaration.Type.NameToken.Text}<…> は、構造体のフィールドにできません。値を持たせるなら、型引数の型をそのまま書いてください。");
                }

                if (field && modifiers.FirstOrDefault(t => t.Text is "__ref" or "__constref") is { } reference)
                {
                    Report(SlangRuleDescriptors.ModifierNotAllowed, reference, $"構造体のフィールドに {reference.Text} は書けません。");
                }
            }
        }
    }

    /// <summary>同じ入れ物 (ファイル・名前空間・構造体) の、名前と仮引数の型が同じ関数の 2 つ目の本体を報告する。</summary>
    private static void ReportRedefinitions(SyntaxTreeAnalysisContext context, ShaderCompilation compilation, CompilationUnitSyntax root, Semantics.Conditional.ConditionMap conditions)
    {
        Dictionary<(object Container, string Key), FunctionDeclarationSyntax> first = [];

        foreach (FunctionDeclarationSyntax function in root.DescendantNodesAndSelf().OfType<FunctionDeclarationSyntax>())
        {
            if (function.Body is null
                || function.Parent is not (CompilationUnitSyntax or NamespaceDeclarationSyntax or StructDeclarationSyntax) || function.Parent is null
                || !function.GenericParameterTokens.IsDefaultOrEmpty
                || !function.SpecializationTokens.IsDefaultOrEmpty
                || function.Attributes.Any(a => a.Name is "require" or "__target_intrinsic" or "TargetIntrinsic" or "__specialized_for_target" or "SpecializedForTarget")
                || function.ModifierTokens.Any(t => t.Text is "__target_intrinsic" or "extern" or "export" or "__specialized_for_target")
                || HasWhereClause(function)
                || !conditions.IsAlwaysPresent(function))
            {
                continue;
            }

            // __prefix と __postfix の演算子 (operator++) は、仮引数が同じでも別のもの。x : in out int は inout と同じ。
            string key = (function.ModifierTokens.Any(t => t.Text == "__postfix") ? "postfix " : "") + function.Name + "(" + string.Join(",", function.ParameterList.Select(p =>
                string.Join(" ", p.ModifierTokens.Select(t => t.Text).Where(t => t is "in" or "out" or "inout" or "ref" or "constref")).Replace("in out", "inout", StringComparison.Ordinal)
                + " " + p.Type.Name + string.Concat(p.Type.TemplateArguments.Select(t => t.Text)) + string.Concat(p.Type.NestedTypeTokens.Select(t => t.Text))
                + new string('[', p.ArrayDimensions))) + ")";

            if (!first.TryAdd((function.Parent, key), function)
                && compilation.IsReportable(function.NameToken)
                && function.NameToken.GetLocation() is { } location
                && first[(function.Parent, key)].NameToken.GetLocation() is { } earlier)
            {
                context.ReportDiagnostic(Diagnostic.Create(HlslRuleDescriptors.Redeclaration, location, function.Name,
                    (earlier.LineSpan.Start.Line + 1).ToString(System.Globalization.CultureInfo.InvariantCulture)));
            }
        }
    }

    /// <summary>仮引数の閉じ括弧と本体の間に where の制約があるか。</summary>
    private static bool HasWhereClause(FunctionDeclarationSyntax function)
    {
        bool after = false;

        foreach (ShaderSyntaxToken token in function.DescendantTokens())
        {
            if (ReferenceEquals(token, function.CloseParen))
            {
                after = true;
            }
            else if (function.Body is not null && ReferenceEquals(token, function.Body.OpenBrace))
            {
                return false;
            }
            else if (after && token.Text == "where")
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>要素の型を ITexelElement に限る型 (テクスチャ・組み合わせたサンプラー・型付きバッファー)。Feedback のテクスチャは要素の型の代わりに種類を書くので除く。</summary>
    private static bool IsTexelContainer(string name)
        => !name.StartsWith("Feedback", StringComparison.Ordinal)
           && (name is "Buffer" or "RWBuffer" or "RasterizerOrderedBuffer"
               || (name.Contains("Texture", StringComparison.Ordinal) && name != "TextureBuffer" && (ShaderResources.Contains(name) || UnorderedResources.Contains(name)))
               || (name.StartsWith("Sampler", StringComparison.Ordinal) && name.Length > 7 && char.IsDigit(name[7]) && !name.EndsWith("Shadow", StringComparison.Ordinal)));

    /// <summary>テクスチャの要素にできない型なら、その説明 (行列・構造体など)。できるか分からなければ <see langword="null"/>。</summary>
    private static string? DescribeNonTexel(ShaderCompilation compilation, AnalyzedProgram program, Dictionary<string, StructDeclarationSyntax> structs, string name)
    {
        if (program.Tree.Root.TypeAliases.ContainsKey(name))
        {
            return null;
        }

        if (compilation.TypeSystem.TryDescribeNumeric(name, out Semantics.Symbols.ShaderNumericShape shape))
        {
            return shape.Kind == Semantics.Symbols.ShaderNumericKind.Matrix ? "行列"
                : shape.BaseName == "bool" ? "真偽値"
                : null;
        }

        if (IsResource(name))
        {
            return "資源の型";
        }

        return structs.TryGetValue(name, out StructDeclarationSyntax? structure) && structure.Keyword.Text is "struct" or "class" ? "構造体" : null;
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
        Action<DiagnosticDescriptor, ShaderSyntaxToken?, string, TokenFix?> report)
    {
        ImmutableArray<ShaderSyntaxToken> modifiers = declaration.ModifierTokens;

        if (!isStatic && !declaration.Type.IsPointer && modifiers.FirstOrDefault(t => t.Text == "constexpr") is { } constexpr)
        {
            report(SlangRuleDescriptors.ModifierNotAllowed, constexpr, $"関数の外の constexpr の '{declarator.Name}' には static が必要です (static constexpr と書きます)。",
                TokenFixes.InsertBefore(constexpr, "static ", "static を付ける"));
        }
        else if (!isStatic && declarator.Initializer is not null && modifiers.FirstOrDefault(t => t.Text == "const") is { } constant)
        {
            report(SlangRuleDescriptors.ModifierNotAllowed, constant, $"関数の外の const の '{declarator.Name}' には static が必要です (static const と書きます)。static の無い const は uniform になり、初期値を持てません。",
                TokenFixes.InsertBefore(constant, "static ", "static を付ける"));
        }
        else if (declarator.Initializer is not null && modifiers.FirstOrDefault(t => t.Text == "uniform") is { } uniform)
        {
            report(SlangRuleDescriptors.ModifierNotAllowed, uniform, $"uniform の '{declarator.Name}' に初期値は書けません。値はアプリケーションから入ります。", null);
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
