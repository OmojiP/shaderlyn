using System.Collections.Immutable;
using Shaderlyn.Core.Analysis;
using Shaderlyn.Rules;
using Shaderlyn.Unity.Rules;
using Shaderlyn.Unreal.Rules;

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
        new DuplicatedByteOrderMarkAnalyzer(),

        // ShaderLab と HLSL を突き合わせて初めて検査できるルール。
        // セマンティックモデルが渡されていない場合、これらは何も報告しない。
        new SkippedCheckAnalyzer(),
        new UnexploredSymbolAnalyzer(),
        new ErrorConfigurationAnalyzer(),
        new UnresolvedConditionAnalyzer(),
        new PropertyUniformAnalyzer(),
        new UnusedPropertyAnalyzer(),
        new MaterialConstantBufferAnalyzer(),

        // HLSL のコードを対象にするルール。
        new DuplicateSemanticAnalyzer(),
        new ShaderSymbolAnalyzer(),
        new IncludePathAnalyzer(),
        new FallbackIncludeAnalyzer(),
        new EntryPointAnalyzer(),
        new SystemValueSemanticAnalyzer(),
        new MeshShaderSignatureAnalyzer(),
        new DefaultParameterOrderAnalyzer(),
        new Rules.Slang.SlangEntryPointVaryingAnalyzer(),
        new ShaderStageAnalyzer(),
        new CompilerPragmaAnalyzer(),
        new Native16BitConstantBufferAnalyzer(),
        new PragmaOnceAnalyzer(),
        new KeywordErrorAnalyzer(),
        new Utf16FileAnalyzer(),
        new FunctionDefinitionAnalyzer(),
        new VariableDeclarationPresenceAnalyzer(),
        new MemberAccessAnalyzer(),
        new CallSignatureAnalyzer(),
        new StructOperatorAnalyzer(),

        // GLSL にしか無いルール。
        new Rules.Glsl.GlslBuiltinRequirementAnalyzer(),
        new Rules.Glsl.GlslDeclarationRequirementAnalyzer(),
        new Rules.Glsl.GlslExpressionRequirementAnalyzer(),
        new Rules.Glsl.GlslPrecisionAnalyzer(),
        new Rules.Glsl.GlslLayoutAnalyzer(),
        new Rules.Glsl.GlslOperatorAnalyzer(),
        new Rules.Glsl.GlslBuiltinCallAnalyzer(),
        new Rules.Glsl.GlslBuiltinArgumentAnalyzer(),
        new Rules.Glsl.GlslSmallTypeStorageAnalyzer(),
        new Rules.Glsl.GlslCallLocationAnalyzer(),
        new Rules.Glsl.GlslVariableArgumentAnalyzer(),
        new Rules.Glsl.GlslIndexRangeAnalyzer(),
        new Rules.Glsl.GlslReservedNameAnalyzer(),
        new Rules.Glsl.GlslInterfaceRequirementAnalyzer(),
        new Rules.Glsl.GlslUnsizedArrayAnalyzer(),
        new Rules.Glsl.GlslArraySizeAnalyzer(),
        new Rules.Glsl.GlslAccessAnalyzer(),
        new Rules.Glsl.GlslDeclarationTypeAnalyzer(),
        new Rules.Glsl.GlslFunctionNameAnalyzer(),
        new Rules.Glsl.GlslConstantExpressionAnalyzer(),
        new Rules.Glsl.GlslArrayConstructorAnalyzer(),
        new Rules.Glsl.GlslOverloadAnalyzer(),
        new Rules.Glsl.GlslPreprocessorAnalyzer(),
        new Rules.Glsl.GlslBuiltinRedeclarationAnalyzer(),
        new Rules.Glsl.GlslSwitchAnalyzer(),
        new Rules.Glsl.GlslMainAnalyzer(),
        new Rules.Glsl.GlslFunctionRedeclarationAnalyzer(),
        new Rules.Glsl.GlslSamplerConstructorAnalyzer(),
        new Rules.Glsl.GlslWorkGroupSizeAnalyzer(),
        new Rules.Glsl.GlslTypeParameterAnalyzer(),
        new Rules.Glsl.GlslCooperativeMatrixAnalyzer(),
        new Rules.Glsl.GlslLongVectorAnalyzer(),
        new Rules.Glsl.GlslSmallFloatAnalyzer(),
        new Rules.Glsl.GlslTensorAnalyzer(),
        new Rules.Glsl.GlslBufferReferenceAnalyzer(),
        new Rules.Glsl.GlslRayTracingAnalyzer(),
        new Rules.Glsl.GlslStringArgumentAnalyzer(),
        new Rules.Glsl.Glsl64BitIndexingAnalyzer(),
        new Rules.Glsl.GlslCooperativeVectorAnalyzer(),
        new Rules.Glsl.GlslArrayRedeclarationAnalyzer(),
        new Rules.Glsl.GlslComputeDerivativeAnalyzer(),
        new Rules.Glsl.GlslArrayValueAnalyzer(),
        new Rules.Glsl.GlslArrayAssignmentAnalyzer(),
        new Rules.Glsl.GlslArgumentQualifierAnalyzer(),
        new Rules.Godot.GodotOperatorAnalyzer(),
        new Rules.Godot.GodotBuiltinCallAnalyzer(),
        new Rules.Godot.GodotStageAnalyzer(),
        new Rules.Godot.GodotDeclarationAnalyzer(),
        new Rules.Godot.GodotStatementAnalyzer(),
        new Rules.Godot.GodotAccessAnalyzer(),
        new Rules.Godot.GodotIndexAnalyzer(),
        new Rules.Godot.GodotNameAnalyzer(),
        new Rules.Godot.GodotArrayAnalyzer(),
        new Rules.Godot.GodotExpressionAnalyzer(),
        new Rules.Godot.GodotSamplerAnalyzer(),
        new Rules.Wgsl.WgslExtensionAnalyzer(),
        new Rules.Wgsl.WgslDeclarationAnalyzer(),
        new Rules.Wgsl.WgslAssignmentAnalyzer(),
        new Rules.Wgsl.WgslConstantAnalyzer(),
        new Rules.Wgsl.WgslRecursionAnalyzer(),
        new Rules.Wgsl.WgslTypeAnalyzer(),
        new Rules.Wgsl.WgslEntryPointAnalyzer(),
        new Rules.Wgsl.WgslLayoutAnalyzer(),
        new Rules.Wgsl.WgslAttributeAnalyzer(),
        new Rules.Wgsl.WgslCallAnalyzer(),
        new Rules.Slang.SlangLiteralAnalyzer(),
        new Rules.Slang.SlangDeclarationPlacementAnalyzer(),
        new Rules.Slang.SlangStatementAnalyzer(),
        new Rules.Slang.SlangImportScopeAnalyzer(),
        new Rules.Slang.SlangCustomDerivativeAnalyzer(),
        new Rules.Slang.SlangDifferentialTypeAnalyzer(),
        new Rules.Slang.SlangBuiltinFormAnalyzer(),
        new Rules.Slang.SlangControlFlowAnalyzer(),
        new Rules.Slang.SlangUnsizedTypeAnalyzer(),
        new Rules.Slang.SlangDeclarationFormAnalyzer(),
        new Rules.Slang.SlangErrorHandlingAnalyzer(),
        new Rules.Slang.SlangMemberUseAnalyzer(),
        new Rules.Slang.SlangTypeDeclarationAnalyzer(),
        new Rules.Slang.SlangGenericConversionAnalyzer(),
        new Rules.Slang.SlangConformanceAnalyzer(),
        new Rules.Slang.SlangDerivativeAnalyzer(),
        new Rules.Slang.SlangUserAttributeAnalyzer(),
        new Rules.Slang.SlangInitializerListAnalyzer(),
        new Rules.Slang.SlangConstantArgumentAnalyzer(),
        new Rules.Slang.SlangModuleErrorAnalyzer(),
        new Rules.Slang.SlangPackAnalyzer(),
        new Rules.Slang.SlangPointerAnalyzer(),
        new Rules.Slang.SlangLambdaAnalyzer(),
        new Rules.Slang.SlangMutatingCallAnalyzer(),
        new Rules.Slang.SlangGenericDeclarationAnalyzer(),
        new Rules.Slang.SlangVisibilityAnalyzer(),
        new Rules.Slang.SlangRemovedDeclarationAnalyzer(),
        new Rules.Slang.SlangDeclarationAnalyzer(),
        new Rules.Slang.SlangConformanceCycleAnalyzer(),
        new Rules.Slang.SlangOperatorAnalyzer(),
        new Rules.Slang.SlangMutationAnalyzer(),
        new CompileTargetAnalyzer(),
        new VectorLogicalOperatorAnalyzer(),
        new LoopVariableScopeAnalyzer(),
        new UserTypeConversionAnalyzer(),
        new ValueConversionAnalyzer(),
        new UndeclaredIdentifierAnalyzer(),
        new RedeclarationAnalyzer(),
        new DeclarationSyntaxAnalyzer(),

        // レンダーパイプライン固有のルール。
        new SrpBatcherAnalyzer(),
        new ObsoleteApiAnalyzer(),

        // Unreal Engine の環境だけのルール。
        new GeneratedIncludeAnalyzer(),

        // 設定ファイルに書かれたプロジェクト固有のルール。
        // 定義が無ければ何も報告しない。
    ];
}
