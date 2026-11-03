using System.Collections.Immutable;
using Shaderlyn.CFamily.Syntax;
using Shaderlyn.Core.Analysis;
using Shaderlyn.Core.Diagnostics;
using Shaderlyn.Semantics;
using Shaderlyn.Semantics.Programs;
using Shaderlyn.Semantics.Symbols;

namespace Shaderlyn.Rules.Glsl;

/// <summary>
/// 組み込みの関数と変数に要る拡張と SPIR-V のバージョンを検査する (GL0001)。
/// </summary>
/// <remarks>
/// <para>
/// 拡張で足される組み込み (<c>subgroupAdd</c> は GL_KHR_shader_subgroup_arithmetic、<c>gl_SubgroupSize</c> は GL_KHR_shader_subgroup_basic) は、
/// その拡張を有効にしてから使わなければならない (glslang の handleFunctionCall と handleVariable の <c>requireExtensions</c>)。
/// どの組み込みにどの拡張が要るかは、glslang から作った表 (<see cref="GlslBuiltins"/>) にある。
/// </para>
/// <para>
/// サブグループの関数は SPIR-V 1.3 からで、8・16・64 ビットの値を渡すには GL_EXT_shader_subgroup_extended_types_* が要る
/// (glslang の builtInOpCheck)。
/// </para>
/// <para>
/// 利用者が同じ名前の関数を宣言していれば、組み込みとは見なさない。
/// </para>
/// </remarks>
internal sealed class GlslBuiltinRequirementAnalyzer : SemanticRuleAnalyzer
{
    /// <inheritdoc/>
    protected override bool AnalyzesGlsl => true;

    /// <inheritdoc/>
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [GlslRuleDescriptors.NotAvailable, GlslRuleDescriptors.NotAvailableInStage];

    /// <inheritdoc/>
    protected override void Analyze(SyntaxTreeAnalysisContext context, ShaderCompilation compilation)
    {
        if (!compilation.IsGlsl)
        {
            return;
        }

        HashSet<(string? File, int Start, string Message)> reported = [];
        Dictionary<AnalyzedProgram, GlslRequirements?> requirements = [];

        foreach ((ShaderSyntaxNode node, AnalyzedProgram program) in compilation.EnumerateRuleNodes())
        {
            if (!requirements.TryGetValue(program, out GlslRequirements? requirement))
            {
                requirement = requirements[program] = GlslRequirements.Create(context, compilation, program, reported);
            }

            if (requirement is null || compilation.TypeSystem.ForTree(program.Tree) is not GlslTypeSystem types)
            {
                continue;
            }

            switch (node)
            {
                case InvocationExpressionSyntax { Target: IdentifierExpressionSyntax callee } invocation:
                    AnalyzeCall(compilation, program, types, requirement, invocation, callee);
                    break;

                // 利用者が宣言し直した組み込み (GL_EXT_spirv_intrinsics の spirv_decorate で宣言した gl_BaryCoordSmoothAMD など) は、その宣言を使う。
                case IdentifierExpressionSyntax identifier
                    when identifier.Name.StartsWith("gl_", StringComparison.Ordinal)
                         && !(identifier.Parent is InvocationExpressionSyntax call && ReferenceEquals(call.Target, identifier))
                         && types.Builtins.GetVariable(identifier.Name) is { Extensions.IsEmpty: false } variable
                         && !program.Tree.Root.Declarations.OfType<VariableDeclarationSyntax>().Any(d => d.Variables.Any(v => v.Name == identifier.Name)):
                    requirement.RequireExtensions(identifier.Identifier, variable.Extensions, $"'{identifier.Name}'");
                    break;

                // 配列の大きさに書いた組み込みの定数 (int a[gl_MaxTransformFeedbackBuffers];)。大きさのトークンは式の構文木にならない。
                case VariableDeclaratorSyntax declarator when !declarator.ArrayRankTokens.IsEmpty:
                    foreach (ShaderSyntaxToken token in declarator.ArrayRankTokens)
                    {
                        if (token.Kind == ShaderSyntaxKind.IdentifierToken
                            && token.Text.StartsWith("gl_", StringComparison.Ordinal)
                            && types.Builtins.GetVariable(token.Text) is { Extensions.IsEmpty: false } constant)
                        {
                            requirement.RequireExtensions(token, constant.Extensions, $"'{token.Text}'");
                        }
                    }

                    break;

                // 別のバージョン・プロファイル・ステージにはあるが、この構成には無い組み込み変数 (ES の gl_ClipDistance など)。glslang は「undeclared identifier」にする。
                case IdentifierExpressionSyntax identifier
                    when identifier.Name.StartsWith("gl_", StringComparison.Ordinal)
                         && !(identifier.Parent is InvocationExpressionSyntax call && ReferenceEquals(call.Target, identifier))
                         && types.IsUnavailableBuiltinVariable(identifier.Name)
                         && compilation.GetExpressionTypeBinder(program).ResolveName(identifier).Kind == DeclaredNameKind.NotFound:
                    if (types.IsBuiltinVariableOfOtherStage(identifier.Name) && requirement.Stage is { } stage)
                    {
                        requirement.Report(GlslRuleDescriptors.NotAvailableInStage, identifier.Identifier, $"組み込みの変数 '{identifier.Name}'", GlslRequirements.DescribeStage(stage));
                    }
                    else
                    {
                        requirement.Report(GlslRuleDescriptors.NotAvailable, identifier.Identifier, $"組み込みの変数 '{identifier.Name}' は、この #version とプロファイルにはありません。");
                    }

                    break;

                // 別の構成にはあるが、この構成には無い組み込みのブロックのメンバー (ES の gl_in[i].gl_ClipDistance など)。
                // 利用者がブロックを宣言し直していれば、その宣言のメンバーを使う。
                case MemberAccessExpressionSyntax access
                    when BlockVariable(access.Target) is { } block
                         && types.IsUnavailableBuiltinMember(block.Name, access.NameToken.Text)
                         && !IsRedeclaredBlock(program, block.Name):
                    requirement.Report(
                        GlslRuleDescriptors.NotAvailable,
                        access.NameToken,
                        $"組み込みのブロック '{block.Name}' のメンバー '{access.NameToken.Text}' は、この #version とプロファイルにはありません。");
                    break;

                default:
                    break;
            }
        }
    }

    /// <summary>メンバーを引く式の、組み込みのブロックの変数 (<c>gl_in</c> / <c>gl_in[i]</c>)。</summary>
    private static IdentifierExpressionSyntax? BlockVariable(ExpressionSyntax target) => target switch
    {
        IdentifierExpressionSyntax { Name: var name } identifier when name.StartsWith("gl_", StringComparison.Ordinal) => identifier,
        ElementAccessExpressionSyntax { Target: var inner } => BlockVariable(inner),
        _ => null,
    };

    /// <summary>組み込みのブロックの変数を、利用者が宣言し直しているか (<c>in gl_PerVertex { … } gl_in[];</c>)。</summary>
    private static bool IsRedeclaredBlock(AnalyzedProgram program, string name)
        => program.Tree.Root.Declarations.OfType<StructDeclarationSyntax>().Any(s => s.Declarators.Any(d => d.Node is VariableDeclaratorSyntax v && v.Name == name));

    /// <summary>組み込み関数の呼び出しを検査する。</summary>
    private static void AnalyzeCall(
        ShaderCompilation compilation,
        AnalyzedProgram program,
        GlslTypeSystem types,
        GlslRequirements requirement,
        InvocationExpressionSyntax invocation,
        IdentifierExpressionSyntax callee)
    {
        string name = callee.Name;
        ImmutableArray<GlslBuiltinFunction> overloads = types.Builtins.GetFunctions(name);

        if (overloads.IsEmpty || !compilation.GetFunctionDeclarations(name, program).IsEmpty)
        {
            return;
        }

        // glslang は拡張を関数の名前ごとに決める (setFunctionExtensions)。拡張の要らない形が 1 つでもあれば、要らないものとする。
        if (overloads.All(o => !o.Extensions.IsEmpty))
        {
            requirement.RequireExtensions(callee.Identifier, [.. overloads.SelectMany(o => o.Extensions).Distinct(StringComparer.Ordinal)], $"'{name}'");
        }

        AnalyzeArgumentTypes(compilation.GetExpressionTypeBinder(program), types, requirement, invocation, callee);

        if (!name.StartsWith("subgroup", StringComparison.Ordinal))
        {
            return;
        }

        requirement.RequireSpirv(callee.Identifier, 103, $"サブグループの関数 '{name}'");

        if (invocation.ArgumentExpressions.FirstOrDefault() is not { } first
            || compilation.GetExpressionTypeBinder(program).GetEvaluatorFor(first).Evaluate(first) is not { } type
            || !types.TryDecomposeNumeric(type, out string element, out _))
        {
            return;
        }

        string? extension = element switch
        {
            "int8_t" or "uint8_t" => "GL_EXT_shader_subgroup_extended_types_int8",
            "int16_t" or "uint16_t" => "GL_EXT_shader_subgroup_extended_types_int16",
            "int64_t" or "uint64_t" => "GL_EXT_shader_subgroup_extended_types_int64",
            "float16_t" => "GL_EXT_shader_subgroup_extended_types_float16",
            _ => null,
        };

        if (extension is not null)
        {
            requirement.RequireExtensions(callee.Identifier, [extension], $"サブグループの関数に {type} を渡すこと");
        }
    }

    /// <summary>
    /// 実引数の型で決まる、組み込み関数に要る拡張 (glslang の builtInOpCheck)。
    /// </summary>
    /// <remarks>
    /// float のアトミック (GL_EXT_shader_atomic_float / float2)、64 ビットの整数のアトミック、メモリのスコープを取るアトミック (GL_KHR_memory_scope_semantics)、
    /// 整数と bool の <c>mix</c> (GL_EXT_shader_integer_mix)、サンプラーを伴わないテクスチャの問い合わせ (GL_EXT_samplerless_texture_functions)、
    /// 影のサンプラーの配列とキューブの <c>texture</c> / <c>textureLod</c> (GL_EXT_texture_shadow_lod) を見る。
    /// 実引数の型が分からなければ検査しない。
    /// </remarks>
    private static void AnalyzeArgumentTypes(
        ExpressionTypeBinder binder,
        GlslTypeSystem types,
        GlslRequirements requirement,
        InvocationExpressionSyntax invocation,
        IdentifierExpressionSyntax callee)
    {
        ExpressionSyntax[] arguments = [.. invocation.ArgumentExpressions];
        string name = callee.Name;
        ShaderSyntaxToken at = callee.Identifier;

        string? TypeOf(int index)
            => index < arguments.Length && binder.GetTypesByConfiguration(arguments[index]) is [{ Type: { } type }] ? type : null;

        (string Base, int Components)? Numeric(int index)
            => TypeOf(index) is { } type && types.TryDecomposeNumeric(type, out string element, out int components) ? (element, components) : null;

        static bool IsFloating(string element) => element is "float" or "double" or "float16_t" || element.StartsWith("bfloat", StringComparison.Ordinal);

        switch (name)
        {
            case "atomicAdd" or "atomicMin" or "atomicMax" or "atomicAnd" or "atomicOr" or "atomicXor" or "atomicExchange"
                or "atomicCompSwap" or "atomicLoad" or "atomicStore":
            {
                if (Numeric(0) is not { } value)
                {
                    break;
                }

                bool addOrExchange = name is "atomicAdd" or "atomicExchange";
                bool float2Ops = name is "atomicAdd" or "atomicExchange" or "atomicLoad" or "atomicStore" or "atomicMin" or "atomicMax";

                if (arguments.Length > 3)
                {
                    requirement.RequireExtensions(at, ["GL_KHR_memory_scope_semantics"], $"'{name}'");

                    if (name is "atomicAdd" or "atomicExchange" or "atomicLoad" or "atomicStore" && value.Base is "float" or "double")
                    {
                        requirement.RequireExtensions(at, ["GL_EXT_shader_atomic_float"], $"'{name}'");
                    }
                    else if (float2Ops && IsFloating(value.Base))
                    {
                        requirement.RequireExtensions(at, ["GL_EXT_shader_atomic_float2"], $"'{name}'");
                    }
                }
                else if (value.Base is "int64_t" or "uint64_t")
                {
                    requirement.RequireExtensions(at, ["GL_NV_shader_atomic_int64", "GL_EXT_shader_atomic_int64"], $"'{name}'");
                }
                else if (name is "atomicAdd" or "atomicExchange" or "atomicMin" or "atomicMax" && value.Base == "float16_t" && value.Components is 2 or 4)
                {
                    requirement.RequireExtensions(at, ["GL_NV_shader_atomic_fp16_vector"], $"'{name}'");
                }
                else if (addOrExchange && value.Base is "float" or "double")
                {
                    requirement.RequireExtensions(at, ["GL_EXT_shader_atomic_float"], $"'{name}'");
                }
                else if (float2Ops && IsFloating(value.Base))
                {
                    requirement.RequireExtensions(at, ["GL_EXT_shader_atomic_float2"], $"'{name}'");
                }

                break;
            }

            case "imageAtomicAdd" or "imageAtomicMin" or "imageAtomicMax" or "imageAtomicExchange" or "imageAtomicLoad" or "imageAtomicStore":
            {
                // 浮動小数点数のイメージ (image2D など。iimage2D / uimage2D でないもの)。
                if (TypeOf(0) is not { } image || !image.StartsWith("image", StringComparison.Ordinal))
                {
                    break;
                }

                // rg16f / rgba16f のイメージに f16vec2 / f16vec4 を渡すものは、GL_NV_shader_atomic_fp16_vector で使える。
                if (Enumerable.Range(1, arguments.Length - 1).Any(i => Numeric(i) is { Base: "float16_t", Components: 2 or 4 }))
                {
                    if (name is "imageAtomicAdd" or "imageAtomicExchange" or "imageAtomicMin" or "imageAtomicMax")
                    {
                        requirement.RequireExtensions(at, ["GL_NV_shader_atomic_fp16_vector"], $"'{name}'");
                    }
                }
                else if (name is "imageAtomicAdd" or "imageAtomicLoad" or "imageAtomicStore")
                {
                    requirement.RequireExtensions(at, ["GL_EXT_shader_atomic_float"], $"'{name}'");
                }
                else if (name is "imageAtomicMin" or "imageAtomicMax")
                {
                    requirement.RequireExtensions(at, ["GL_EXT_shader_atomic_float2"], $"'{name}'");
                }

                break;
            }

            case "mix" when arguments.Length == 3 && Numeric(0) is { } x && Numeric(1) is { } y && Numeric(2) is { Base: "bool" }:
                if (requirement.IsEs && requirement.Version < 310 && x.Base != "float" && y.Base != "float")
                {
                    requirement.RequireExtensions(at, ["GL_EXT_shader_integer_mix"], "整数と bool の mix");
                }

                if (!requirement.IsEs && requirement.Version < 450 && x.Base is not ("float" or "double") && y.Base is not ("float" or "double"))
                {
                    requirement.RequireExtensions(at, ["GL_EXT_shader_integer_mix"], "整数と bool の mix");
                }

                break;

            // サンプラーを伴わないテクスチャ (texture2D など)。texelFetch の textureBuffer は拡張が要らない。
            case "textureSize" or "textureQueryLevels" or "textureSamples" or "texelFetch" or "texelFetchOffset"
                when TypeOf(0) is { } texture && IsSeparateTexture(texture)
                     && !(name is "texelFetch" or "texelFetchOffset" && texture.Contains("Buffer", StringComparison.Ordinal)):
                requirement.RequireExtensions(at, ["GL_EXT_samplerless_texture_functions"], $"'{name}'");
                break;

            case "texture" or "textureLod" or "textureOffset" or "textureLodOffset"
                when TypeOf(0) is { } sampler && sampler.Contains("Shadow", StringComparison.Ordinal) && Numeric(1) is { Base: "float", Components: 4 }:
            {
                bool arrayed = sampler.Contains("Array", StringComparison.Ordinal);
                bool cube = sampler.Contains("Cube", StringComparison.Ordinal);
                bool twoD = sampler.Contains("2D", StringComparison.Ordinal);
                bool needs = name switch
                {
                    "texture" => arguments.Length > 2 && ((twoD && arrayed) || (cube && arrayed && arguments.Length > 3)),
                    "textureLod" => arguments.Length > 2 && ((twoD && arrayed) || (cube && arrayed && arguments.Length > 3) || cube),
                    _ => twoD && arrayed && arguments.Length == 4,
                };

                if (needs)
                {
                    requirement.RequireExtensions(at, ["GL_EXT_texture_shadow_lod"], $"影のサンプラー {sampler} の '{name}'");
                }

                break;
            }

            case "textureGather" or "textureGatherOffset" or "textureGatherOffsets":
            {
                string feature = $"'{name}'";
                requirement.ProfileRequires(at, GlslProfileMask.Es, 310, [], feature);

                if (TypeOf(0) is not { } sampler)
                {
                    break;
                }

                bool shadow = sampler.Contains("Shadow", StringComparison.Ordinal);
                bool rect = sampler.Contains("Rect", StringComparison.Ordinal);
                bool twoD = sampler.Contains("2D", StringComparison.Ordinal) && !rect;
                bool basic = name switch
                {
                    "textureGather" => arguments.Length <= 2 && !rect && !shadow,
                    "textureGatherOffset" => twoD && !shadow && arguments.Length == 3,
                    _ => false,
                };

                requirement.ProfileRequires(
                    at,
                    GlslProfileMask.NotEs,
                    400,
                    basic ? ["GL_ARB_texture_gather", "GL_ARB_gpu_shader5", "GL_NV_gpu_shader5"] : ["GL_ARB_gpu_shader5", "GL_NV_gpu_shader5"],
                    feature);

                int offset = shadow ? 3 : 2;

                if (name == "textureGatherOffset" && offset < arguments.Length && new GlslConstantEvaluator(binder).IsCertainlyNotConstant(arguments[offset]))
                {
                    requirement.ProfileRequires(at, GlslProfileMask.Es, 320, ["GL_EXT_gpu_shader5", "GL_OES_gpu_shader5"], "textureGatherOffset の定数でないオフセット");
                }

                break;
            }

            case "textureSamples" or "imageSamples":
                requirement.ProfileRequires(at, GlslProfileMask.NotEs, 450, ["GL_ARB_shader_texture_image_samples"], $"'{name}'");
                break;

            default:
                break;
        }
    }

    /// <summary>サンプラーを伴わないテクスチャの型か (<c>texture2D</c>、<c>itexture3D</c> など)。</summary>
    private static bool IsSeparateTexture(string type)
    {
        int texture = type.IndexOf("texture", StringComparison.Ordinal);
        return texture >= 0 && texture <= 3 && !type.Contains("sampler", StringComparison.Ordinal);
    }
}
