using System.Collections.Immutable;
using Shaderlyn.CFamily.Syntax;
using Shaderlyn.Core.Analysis;
using Shaderlyn.Core.Diagnostics;
using Shaderlyn.Hlsl.Parsing;
using Shaderlyn.Semantics;
using Shaderlyn.Semantics.Environments;
using Shaderlyn.Semantics.Symbols;

namespace Shaderlyn.Rules;

/// <summary>
/// システム値のセマンティクス (<c>SV_Depth</c> など) が、エントリポイントのステージと向きで使用できるかを検査する (CF0302)。
/// </summary>
/// <remarks>
/// <para>
/// <b>使用できるかは表で決める。</b> 表は FXC・DXC・slangc に掛けて作った (<see cref="SystemValueSemantics"/>)。
/// 3 つのコンパイラで表が違う (slangc は頂点シェーダーの入力に SV_Position を使用できない、FXC はシェーダーモデル 6 のものを使用できない、など) ので、
/// ファイルをコンパイルするコンパイラの表を使う。表に無い組み合わせについては何も言わない。
/// </para>
/// <para>
/// <b>ステージが分かるエントリポイントだけを見る。</b>
/// ステージは <c>[shader("vertex")]</c> の属性か、環境が読んだエントリポイントのステージ
/// (Unity の <c>#pragma vertex</c>、設定ファイルの target) で決める。
/// </para>
/// <para>
/// 仮引数と戻り値に直接付いたセマンティクスと、そこで受け渡す構造体のフィールド (入れ子と基底の構造体を含む) を見る。
/// ジオメトリ・ハル・ドメイン・メッシュシェーダーは、値の受け渡しの形 (頂点の配列・パッチ・ストリーム・<c>out vertices</c> など) ごとに表を引く。
/// ハルシェーダーのパッチ定数の関数は、<c>[patchconstantfunc("…")]</c> で指定した関数の戻り値を見る。
/// </para>
/// </remarks>
internal sealed class SystemValueSemanticAnalyzer : SemanticRuleAnalyzer
{
    private static readonly ImmutableHashSet<string> GeometryInputSpecifiers = ["point", "line", "triangle", "lineadj", "triangleadj"];

    private static readonly ImmutableHashSet<string> GeometryStreams = ["PointStream", "LineStream", "TriangleStream"];

    /// <inheritdoc/>
    protected override bool AnalyzesSlang => true;

    /// <inheritdoc/>
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [HlslRuleDescriptors.SystemValueSemanticNotAllowed];

    /// <inheritdoc/>
    protected override void Analyze(SyntaxTreeAnalysisContext context, ShaderCompilation compilation)
    {
        // 同じ構造体を複数のコードブロックや複数のエントリポイントで使うので、同じ位置に同じ文を重ねて報告しない。
        HashSet<(string, int, string)> reported = [];

        foreach (AnalyzedProgram program in compilation.Programs)
        {
            ImmutableArray<FunctionDeclarationSyntax> functions = [.. EntryPointAnalyzer.EnumerateFunctions(program.Tree.Root.Declarations).Where(f => f.IsDefinition)];

            foreach ((FunctionDeclarationSyntax function, HlslShaderStage stage) in EntryFunctions(program, functions))
            {
                ImmutableArray<SystemValueCompiler> compilers = CompilersOf(compilation, function, program);

                if (compilers.IsEmpty)
                {
                    continue;
                }

                Checker checker = new(context, program, stage, compilers, reported);
                checker.CheckFunction(function, functions);
            }
        }
    }

    /// <summary>ステージの分かるエントリポイントの関数を列挙する。</summary>
    /// <param name="program">コードブロック。</param>
    /// <param name="functions">コードブロックの関数の定義。</param>
    /// <returns>関数とステージ。</returns>
    internal static IEnumerable<(FunctionDeclarationSyntax Function, HlslShaderStage Stage)> EntryFunctions(
        AnalyzedProgram program,
        ImmutableArray<FunctionDeclarationSyntax> functions)
    {
        HashSet<FunctionDeclarationSyntax> seen = new(ReferenceEqualityComparer.Instance);

        foreach (FunctionDeclarationSyntax function in functions)
        {
            if (function.Attributes.FirstOrDefault(a => a.Name == "shader") is { ArgumentTokens: [{ Kind: ShaderSyntaxKind.StringLiteralToken } literal] }
                && StageOfAttribute(literal.Text.Trim('"')) is { } stage
                && seen.Add(function))
            {
                yield return (function, stage);
            }

            // [outputtopology] と out vertices などを持つ関数は、[shader("mesh")] が無くてもメッシュシェーダーである (slangc も検査する)。
            if (function.Attributes.Any(a => a.Name == "outputtopology")
                && function.ParameterList.Any(p => p.ModifierTokens.Concat(p.Type.ModifierTokens).Any(t => t.Text is "vertices" or "indices" or "primitives")
                                                   || p.Type.Name is "OutputVertices" or "OutputIndices" or "OutputPrimitives")
                && seen.Add(function))
            {
                yield return (function, HlslShaderStage.Mesh);
            }
        }

        foreach (ShaderEntryPoint entryPoint in program.EntryPoints)
        {
            // 同じ名前の定義が複数ある (多重定義) と、どれがエントリポイントか決められない。
            if (entryPoint.Stage is { } stage
                && functions.Where(f => f.Name == entryPoint.Name).ToArray() is [var function]
                && seen.Add(function))
            {
                yield return (function, stage);
            }
        }
    }

    /// <summary><c>[shader("…")]</c> のステージの名前から、ステージを返す。レイトレーシングのステージは <see langword="null"/>。</summary>
    private static HlslShaderStage? StageOfAttribute(string name) => name switch
    {
        "vertex" => HlslShaderStage.Vertex,
        "fragment" or "pixel" => HlslShaderStage.Pixel,
        "compute" => HlslShaderStage.Compute,
        "geometry" => HlslShaderStage.Geometry,
        "hull" => HlslShaderStage.Hull,
        "domain" => HlslShaderStage.Domain,
        "mesh" => HlslShaderStage.Mesh,
        "amplification" or "task" => HlslShaderStage.Amplification,
        _ => null,
    };

    /// <summary>関数をコンパイルするコンパイラ。分からなければ空。</summary>
    internal static ImmutableArray<SystemValueCompiler> CompilersOf(ShaderCompilation compilation, FunctionDeclarationSyntax function, AnalyzedProgram program)
    {
        if (compilation.IsSlang)
        {
            return [SystemValueCompiler.Slang];
        }

        IEnumerable<HlslCompiler> compilers = CompileTargets.Reaching(compilation, function, program).Select(t => t.Dialect.Compiler).ToArray() is { Length: > 0 } reaching
            ? reaching
            : program.Tree.Hlsl().Dialects.Select(d => d.Compiler);

        return [.. compilers.Distinct().Select(c => c == HlslCompiler.Fxc ? SystemValueCompiler.Fxc : SystemValueCompiler.Dxc)];
    }

    /// <summary>1 つのエントリポイントを検査する。</summary>
    private sealed class Checker(
        SyntaxTreeAnalysisContext context,
        AnalyzedProgram program,
        HlslShaderStage stage,
        ImmutableArray<SystemValueCompiler> compilers,
        HashSet<(string, int, string)> reported)
    {
        public void CheckFunction(FunctionDeclarationSyntax function, ImmutableArray<FunctionDeclarationSyntax> functions)
        {
            foreach (ParameterSyntax parameter in function.ParameterList)
            {
                CheckParameter(parameter);
            }

            // 戻り値。ハルシェーダーの戻り値は制御点の出力。
            SystemValueShape returned = stage == HlslShaderStage.Hull ? SystemValueShape.OutControlPoint : SystemValueShape.Out;
            CheckSemantics(function.Semantics, function.NameToken, returned);
            CheckStruct(function.ReturnType.Name, returned, 0);

            // ハルシェーダーのパッチ定数の関数の戻り値。
            if (stage == HlslShaderStage.Hull
                && function.Attributes.FirstOrDefault(a => a.Name == "patchconstantfunc") is { ArgumentTokens: [{ Kind: ShaderSyntaxKind.StringLiteralToken } literal] }
                && functions.Where(f => f.Name == literal.Text.Trim('"')).ToArray() is [var patchConstant])
            {
                CheckSemantics(patchConstant.Semantics, patchConstant.NameToken, SystemValueShape.OutPatchConstant);
                CheckStruct(patchConstant.ReturnType.Name, SystemValueShape.OutPatchConstant, 0);
            }
        }

        private void CheckParameter(ParameterSyntax parameter)
        {
            string[] modifiers = [.. parameter.ModifierTokens.Concat(parameter.Type.ModifierTokens).Select(t => t.Text)];

            if (modifiers.Contains("uniform"))
            {
                return;
            }

            bool output = modifiers.Contains("out") || modifiers.Contains("inout");
            bool input = !modifiers.Contains("out");
            string type = parameter.Type.Name;
            string? element = parameter.Type.TemplateArguments.FirstOrDefault(t => t.Kind == ShaderSyntaxKind.IdentifierToken)?.Text;

            switch (stage)
            {
                case HlslShaderStage.Mesh when modifiers.Contains("vertices") || type == "OutputVertices":
                    CheckStruct(type == "OutputVertices" ? element : type, SystemValueShape.OutVertices, 0);
                    return;

                case HlslShaderStage.Mesh when modifiers.Contains("primitives") || type == "OutputPrimitives":
                    CheckStruct(type == "OutputPrimitives" ? element : type, SystemValueShape.OutPrimitives, 0);
                    return;

                case HlslShaderStage.Mesh when modifiers.Contains("indices") || type == "OutputIndices":
                    CheckStruct(type == "OutputIndices" ? element : type, SystemValueShape.OutIndices, 0);
                    return;

                case HlslShaderStage.Mesh or HlslShaderStage.Amplification when modifiers.Contains("payload"):
                    return;

                case HlslShaderStage.Geometry when modifiers.Any(GeometryInputSpecifiers.Contains):
                    CheckStruct(type, SystemValueShape.InArray, 0);
                    CheckSemantics(parameter.Semantics, parameter.NameToken, SystemValueShape.InArray);
                    return;

                case HlslShaderStage.Geometry when GeometryStreams.Contains(type):
                    CheckStruct(element, SystemValueShape.OutStream, 0);
                    return;

                case HlslShaderStage.Hull or HlslShaderStage.Domain when type is "InputPatch" or "OutputPatch":
                    CheckStruct(element, SystemValueShape.InPatch, 0);
                    return;

                case HlslShaderStage.Domain when input && !output && FindStruct(type) is not null:
                    // ドメインシェーダーが構造体で受け取る入力は、パッチ定数。
                    CheckStruct(type, SystemValueShape.InPatchConstant, 0);
                    return;
            }

            if (input)
            {
                CheckSemantics(parameter.Semantics, parameter.NameToken, SystemValueShape.In);
                CheckStruct(type, SystemValueShape.In, 0);
            }

            if (output)
            {
                CheckSemantics(parameter.Semantics, parameter.NameToken, SystemValueShape.Out);
                CheckStruct(type, SystemValueShape.Out, 0);
            }
        }

        /// <summary>構造体のフィールドを検査する。入れ子の構造体と基底の構造体もたどる。</summary>
        private void CheckStruct(string? name, SystemValueShape shape, int depth)
        {
            if (name is null || depth > 8 || FindStruct(name) is not { } declaration)
            {
                return;
            }

            CheckStruct(declaration.BaseTypeName, shape, depth + 1);

            foreach (VariableDeclarationSyntax field in declaration.Fields)
            {
                foreach (VariableDeclaratorSyntax declarator in field.Declarators.Select(d => d.Node).OfType<VariableDeclaratorSyntax>())
                {
                    CheckSemantics(declarator.Semantics, declarator.NameToken, shape);
                }

                CheckStruct(field.Type.Name, shape, depth + 1);
            }
        }

        private StructDeclarationSyntax? FindStruct(string name)
            => program.Structs.FirstOrDefault(s => s.Keyword.Text is "struct" or "class" && s.Name == name);

        private void CheckSemantics(ImmutableArray<SemanticSyntax> semantics, ShaderSyntaxToken name, SystemValueShape shape)
        {
            foreach (SemanticSyntax semantic in semantics)
            {
                if (!semantic.Name.StartsWith("SV_", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                SystemValueCompiler[] rejecting = shape == SystemValueShape.OutIndices
                    ? [.. compilers.Where(c => IsPerPrimitive(c, semantic.Name))]
                    : [.. compilers.Where(c => SystemValueSemantics.IsAllowed(c, stage, shape, semantic.Name) == false)];

                if (rejecting.Length == 0)
                {
                    continue;
                }

                string message = Describe(semantic.Name, shape, rejecting);
                Location location = name.GetLocation();

                if (reported.Add((location.FilePath, location.Span.Start, message)))
                {
                    context.ReportDiagnostic(Diagnostic.Create(
                        HlslRuleDescriptors.SystemValueSemanticNotAllowed,
                        location,
                        message,
                        string.Join("・", rejecting.Select(NameOf))));
                }
            }
        }

        private string Describe(string semantic, SystemValueShape shape, SystemValueCompiler[] rejecting)
        {
            if (rejecting.All(c => !SystemValueSemantics.IsKnown(c, semantic)))
            {
                return $"'{semantic}' はこのコンパイラでは使用できません。シェーダーモデル 6 以降のセマンティクスです";
            }

            string where = ShapeName(shape);
            string text = $"'{semantic}' は{StageName(stage)}の{where}{(where.EndsWith(')') ? " " : string.Empty)}には使用できません";

            // プリミティブごとの値を頂点やインデックスの出力に書いている (slangc の E30703)。
            if (shape is SystemValueShape.OutVertices or SystemValueShape.OutIndices && rejecting.All(c => IsPerPrimitive(c, semantic)))
            {
                text += "。プリミティブごとの値なので、out primitives の構造体に置いてください";
            }

            return text;
        }

        /// <summary>メッシュシェーダーの out primitives にだけ使用でき、out vertices には使用できない値か。</summary>
        private static bool IsPerPrimitive(SystemValueCompiler compiler, string semantic)
            => SystemValueSemantics.IsAllowed(compiler, HlslShaderStage.Mesh, SystemValueShape.OutPrimitives, semantic) == true
               && SystemValueSemantics.IsAllowed(compiler, HlslShaderStage.Mesh, SystemValueShape.OutVertices, semantic) == false;

        private static string StageName(HlslShaderStage stage) => stage switch
        {
            HlslShaderStage.Vertex => "頂点シェーダー",
            HlslShaderStage.Pixel => "ピクセルシェーダー",
            HlslShaderStage.Compute => "コンピュートシェーダー",
            HlslShaderStage.Geometry => "ジオメトリシェーダー",
            HlslShaderStage.Hull => "ハルシェーダー",
            HlslShaderStage.Domain => "ドメインシェーダー",
            HlslShaderStage.Mesh => "メッシュシェーダー",
            HlslShaderStage.Amplification => "増幅シェーダー",
            _ => stage.ToString(),
        };

        private static string ShapeName(SystemValueShape shape) => shape switch
        {
            SystemValueShape.In => "入力",
            SystemValueShape.Out => "出力",
            SystemValueShape.InArray => "頂点の入力",
            SystemValueShape.OutStream => "ストリームへの出力",
            SystemValueShape.InPatch => "制御点の入力",
            SystemValueShape.OutControlPoint => "制御点の出力",
            SystemValueShape.OutPatchConstant => "パッチ定数の出力",
            SystemValueShape.InPatchConstant => "パッチ定数の入力",
            SystemValueShape.OutVertices => "頂点の出力 (out vertices)",
            SystemValueShape.OutPrimitives => "プリミティブの出力 (out primitives)",
            SystemValueShape.OutIndices => "インデックスの出力 (out indices)",
            _ => shape.ToString(),
        };

        private static string NameOf(SystemValueCompiler compiler) => compiler switch
        {
            SystemValueCompiler.Fxc => "FXC",
            SystemValueCompiler.Dxc => "DXC",
            _ => "slangc",
        };
    }
}
