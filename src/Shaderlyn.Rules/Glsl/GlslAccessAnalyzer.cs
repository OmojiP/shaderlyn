using System.Collections.Immutable;
using Shaderlyn.CFamily.Syntax;
using Shaderlyn.Core.Analysis;
using Shaderlyn.Core.Diagnostics;
using Shaderlyn.Core.Syntax;
using Shaderlyn.Glsl;
using Shaderlyn.Glsl.Parsing;
using Shaderlyn.Glsl.Syntax;
using Shaderlyn.Semantics;
using Shaderlyn.Semantics.Programs;
using Shaderlyn.Semantics.Symbols;

namespace Shaderlyn.Rules.Glsl;

/// <summary>
/// 書き込めない値への書き込み (GL0309) と、writeonly の値の読み取り (GL0310) を検査する。
/// glslang 16.6.0 の ParseContextBase.cpp / ParseHelper.cpp の <c>lValueErrorCheck</c> と <c>rValueErrorCheck</c> を移したもの。
/// </summary>
/// <remarks>
/// <para>
/// 書き込みは、代入の左辺、<c>++</c> / <c>--</c>、利用者の関数の out / inout の仮引数と <c>modf</c> / <c>frexp</c> の第 2 引数に渡す実引数を見る。
/// 書き込む先のもとの変数 (添字・メンバー・成分の選び出しを外したもの) が、const の変数と仮引数、uniform、ステージの入力、
/// readonly の buffer、shaderRecordEXT の buffer なら報告する。変数でない値 (演算の結果、関数の戻り値など) も報告する。
/// テッセレーションの制御シェーダーの、頂点ごとの出力の配列を <c>gl_InvocationID</c> でない添字で引いて書き込むものも報告する。
/// </para>
/// <para>
/// 読み取りは、writeonly の buffer のブロック (かそのメンバー) を、演算子の項・代入の右辺・初期化子・<c>?:</c>・値の作成の実引数に使うものを見る (glslang が検査する所と同じ)。
/// </para>
/// <para>
/// <c>gl_</c> の組み込みの変数と、もとの変数を言い切れない式 (バッファの参照を通るかもしれないもの) は見ない。
/// </para>
/// </remarks>
internal sealed class GlslAccessAnalyzer : SemanticRuleAnalyzer
{
    /// <inheritdoc/>
    protected override bool AnalyzesGlsl => true;

    /// <inheritdoc/>
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [GlslRuleDescriptors.NotWritable, GlslRuleDescriptors.WriteOnlyRead];

    /// <summary>もとの変数の記憶域と修飾子。</summary>
    /// <param name="Name">名前。</param>
    /// <param name="ReadOnlyReason">書き込めない理由。書き込めるなら <see langword="null"/>。</param>
    /// <param name="WriteOnly">writeonly か。</param>
    private readonly record struct Base(string Name, string? ReadOnlyReason, bool WriteOnly);

    /// <summary>入力の組み込みの変数 (ステージの入力の記憶域を持つもの)。</summary>
    private static readonly HashSet<string> BuiltinInputs = new(StringComparer.Ordinal)
    {
        "gl_VertexID", "gl_InstanceID", "gl_VertexIndex", "gl_InstanceIndex", "gl_BaseVertex", "gl_BaseInstance", "gl_DrawID",
        "gl_BaseVertexARB", "gl_BaseInstanceARB", "gl_DrawIDARB", "gl_ViewIndex", "gl_DeviceIndex",
        "gl_FragCoord", "gl_FrontFacing", "gl_PointCoord", "gl_SampleID", "gl_SamplePosition", "gl_SampleMaskIn", "gl_HelperInvocation",
        "gl_PrimitiveIDIn", "gl_InvocationID", "gl_PatchVerticesIn", "gl_TessCoord", "gl_in",
        "gl_GlobalInvocationID", "gl_LocalInvocationID", "gl_WorkGroupID", "gl_LocalInvocationIndex", "gl_NumWorkGroups",
        "gl_NumSubgroups", "gl_SubgroupID", "gl_SubgroupSize", "gl_SubgroupInvocationID",
    };

    /// <inheritdoc/>
    protected override void Analyze(SyntaxTreeAnalysisContext context, ShaderCompilation compilation)
    {
        if (!compilation.IsGlsl)
        {
            return;
        }

        HashSet<(string? File, int Start, string Message)> reported = [];
        Dictionary<AnalyzedProgram, GlslRequirements?> requirements = [];
        Dictionary<AnalyzedProgram, int?> earlyFragmentTests = [];

        foreach ((ShaderSyntaxNode node, AnalyzedProgram program) in compilation.EnumerateRuleNodes())
        {
            if (node is not (AssignmentExpressionSyntax or PrefixUnaryExpressionSyntax or PostfixUnaryExpressionSyntax
                or InvocationExpressionSyntax or BinaryExpressionSyntax or ConditionalExpressionSyntax or VariableDeclaratorSyntax))
            {
                continue;
            }

            if (!requirements.TryGetValue(program, out GlslRequirements? requirement))
            {
                requirement = requirements[program] = GlslRequirements.Create(context, compilation, program, reported);
            }

            if (requirement is null)
            {
                continue;
            }

            if (!earlyFragmentTests.TryGetValue(program, out int? early))
            {
                early = earlyFragmentTests[program] = FindEarlyFragmentTests(program);
            }

            Checker checker = new(requirement, compilation, program, early);

            switch (node)
            {
                case AssignmentExpressionSyntax assignment:
                    checker.CheckWrite(assignment.Left, assignment.OperatorToken);
                    checker.CheckRead(assignment.Right, assignment.OperatorToken);
                    break;

                case PrefixUnaryExpressionSyntax { OperatorToken.Kind: ShaderSyntaxKind.PlusPlusToken or ShaderSyntaxKind.MinusMinusToken } increment:
                    checker.CheckWrite(increment.Operand, increment.OperatorToken);
                    checker.CheckRead(increment.Operand, increment.OperatorToken);
                    break;

                case PrefixUnaryExpressionSyntax unary:
                    checker.CheckRead(unary.Operand, unary.OperatorToken);
                    break;

                case PostfixUnaryExpressionSyntax increment:
                    checker.CheckWrite(increment.Operand, increment.OperatorToken);
                    checker.CheckRead(increment.Operand, increment.OperatorToken);
                    break;

                case BinaryExpressionSyntax { OperatorToken.Kind: not ShaderSyntaxKind.CommaToken } binary:
                    checker.CheckRead(binary.Left, binary.OperatorToken);
                    checker.CheckRead(binary.Right, binary.OperatorToken);
                    break;

                case ConditionalExpressionSyntax conditional:
                    checker.CheckRead(conditional.Condition, conditional.QuestionToken);
                    checker.CheckRead(conditional.WhenTrue, conditional.QuestionToken);
                    checker.CheckRead(conditional.WhenFalse, conditional.QuestionToken);
                    break;

                case VariableDeclaratorSyntax { Initializer: { } initializer } declarator:
                    checker.CheckRead(initializer, declarator.NameToken);
                    break;

                case InvocationExpressionSyntax { Target: IdentifierExpressionSyntax callee } invocation:
                    checker.CheckCall(callee, [.. invocation.ArgumentExpressions]);
                    break;

                default:
                    break;
            }
        }

        // フラグメントシェーダーで gl_FragColor と gl_FragData の両方を使うと、glslang のリンクが「Cannot use both gl_FragColor and gl_FragData」にする。
        foreach (AnalyzedProgram program in compilation.Programs)
        {
            if (requirements.GetValueOrDefault(program) is not { Stage: Shaderlyn.Glsl.GlslShaderStage.Fragment } requirement)
            {
                continue;
            }

            IdentifierExpressionSyntax[] uses =
            [
                .. program.Tree.Root.DescendantNodesAndSelf().OfType<IdentifierExpressionSyntax>()
                    .Where(u => u.Name is "gl_FragColor" or "gl_FragData" && ReferenceEquals(u.Identifier.Source, program.Tree.Text)),
            ];

            if (uses.Any(u => u.Name == "gl_FragColor") && uses.Any(u => u.Name == "gl_FragData"))
            {
                IdentifierExpressionSyntax second = uses.First(u => u.Name != uses[0].Name);
                requirement.Report(GlslRuleDescriptors.NotWritable, second.Identifier, $"'{second.Name}'", "gl_FragColor と gl_FragData の両方は使えません");
            }
        }
    }

    /// <summary>layout(early_fragment_tests) in; の位置 (同じファイルのもの)。無ければ <see langword="null"/>。</summary>
    private static int? FindEarlyFragmentTests(AnalyzedProgram program)
        => program.Tree.Root.DescendantNodesAndSelf()
            .OfType<IncompleteDeclarationSyntax>()
            .Where(d => d.Tokens.Any(t => t.Text == "early_fragment_tests") && ReferenceEquals(d.Tokens[0].Source, program.Tree.Text))
            .Select(d => (int?)d.Tokens[0].Span.Start)
            .Min();

    /// <summary>1 つの木の読み書きを検査する。</summary>
    private sealed class Checker(GlslRequirements requirement, ShaderCompilation compilation, AnalyzedProgram program, int? earlyFragmentTests)
    {
        private readonly ExpressionTypeBinder _binder = compilation.GetExpressionTypeBinder(program);

        /// <summary>バッファの参照 (buffer_reference) を宣言しているか。メンバーの先が参照なら、書き込む先は参照の指す buffer になる。</summary>
        private readonly bool _hasReferences = compilation.CodeTokens.Any(t => t.Text == "buffer_reference");

        /// <summary>layout(early_fragment_tests) in; の位置 (同じファイルのもの)。無ければ <see langword="null"/>。</summary>
        private readonly int? _earlyFragmentTests = earlyFragmentTests;

        /// <summary>関数の呼び出し。値の作成の実引数を読み、out の仮引数に渡す実引数に書く。</summary>
        public void CheckCall(IdentifierExpressionSyntax callee, ExpressionSyntax[] arguments)
        {
            if (GlslKeywords.IsBuiltInType(callee.Name))
            {
                foreach (ExpressionSyntax argument in arguments)
                {
                    CheckRead(argument, callee.Identifier);
                }

                return;
            }

            ImmutableArray<FunctionDeclarationSyntax> functions = compilation.GetFunctionDeclarations(callee.Name, program);

            for (int i = 0; i < arguments.Length; i++)
            {
                bool output = functions.IsEmpty
                    ? callee.Name is "modf" or "frexp" && i == 1
                      || callee.Name is "atomicAdd" or "atomicMin" or "atomicMax" or "atomicAnd" or "atomicOr" or "atomicXor" or "atomicExchange" or "atomicCompSwap" or "atomicStore" && i == 0
                    : functions.Where(f => ParametersOf(f).Count == arguments.Length).ToList() is { Count: > 0 } matching
                      && matching.All(f => ParametersOf(f)[i].ModifierTokens.Any(t => t.Text is "out" or "inout"));

                if (output)
                {
                    CheckWrite(arguments[i], callee.Identifier);
                }
            }
        }

        /// <summary>書き込む先を検査する。</summary>
        public void CheckWrite(ExpressionSyntax target, ShaderSyntaxToken at)
        {
            ExpressionSyntax expression = Strip(target);
            bool viaMember = false;
            string? firstMember = null;

            while (true)
            {
                switch (expression)
                {
                    case MemberAccessExpressionSyntax member:
                        viaMember = true;
                        firstMember = member.NameToken.Text;
                        expression = Strip(member.Target);
                        continue;

                    case ElementAccessExpressionSyntax element:
                        CheckPerVertexOutput(element, at);
                        expression = Strip(element.Target);
                        continue;

                    case IdentifierExpressionSyntax identifier:
                        if (!(viaMember && _hasReferences) && Resolve(identifier, firstMember) is { ReadOnlyReason: { } reason } variable)
                        {
                            requirement.Report(GlslRuleDescriptors.NotWritable, at, $"'{variable.Name}'", reason);
                        }
                        else if (identifier.Name == "gl_FragDepth" && _earlyFragmentTests is { } early
                                 && ReferenceEquals(identifier.Identifier.Source, program.Tree.Text) && early < identifier.Identifier.Span.Start)
                        {
                            // glslang の lValueErrorCheck の「can't modify gl_FragDepth if using early_fragment_tests」。
                            requirement.Report(GlslRuleDescriptors.NotWritable, at, "'gl_FragDepth'", "early_fragment_tests を宣言した後");
                        }

                        return;

                    // 変数でない値。メンバーを通るものは、バッファの参照かもしれないので見ない。
                    case BinaryExpressionSyntax or AssignmentExpressionSyntax or ConditionalExpressionSyntax
                        or InvocationExpressionSyntax or LiteralExpressionSyntax or CastExpressionSyntax
                        or PrefixUnaryExpressionSyntax or PostfixUnaryExpressionSyntax when !viaMember:
                        requirement.Report(GlslRuleDescriptors.NotWritable, at, "この式", "変数でない値");
                        return;

                    default:
                        return;
                }
            }
        }

        /// <summary>読む値を検査する。writeonly の buffer を読むものを報告する。</summary>
        public void CheckRead(ExpressionSyntax value, ShaderSyntaxToken at)
        {
            ExpressionSyntax expression = Strip(value);
            string? firstMember = null;

            while (true)
            {
                switch (expression)
                {
                    case MemberAccessExpressionSyntax member:
                        firstMember = member.NameToken.Text;
                        expression = Strip(member.Target);
                        continue;

                    case ElementAccessExpressionSyntax element:
                        expression = Strip(element.Target);
                        continue;

                    case IdentifierExpressionSyntax identifier:
                        if (Resolve(identifier, firstMember) is { WriteOnly: true } variable)
                        {
                            requirement.Report(GlslRuleDescriptors.WriteOnlyRead, at, variable.Name);
                        }

                        return;

                    default:
                        return;
                }
            }
        }

        /// <summary>テッセレーションの制御シェーダーの頂点ごとの出力は、gl_InvocationID で引いて書き込む。</summary>
        private void CheckPerVertexOutput(ElementAccessExpressionSyntax element, ShaderSyntaxToken at)
        {
            if (requirement.Stage != GlslShaderStage.TessellationControl
                || Strip(element.Target) is not IdentifierExpressionSyntax array
                || Strip(element.Index) is IdentifierExpressionSyntax { Name: "gl_InvocationID" })
            {
                return;
            }

            bool perVertexOutput = array.Name == "gl_out"
                || _binder.ResolveName(array) is { Kind: DeclaredNameKind.Global, Candidates: [{ Declarator: VariableDeclaratorSyntax { Parent: VariableDeclarationSyntax { Parent: CompilationUnitSyntax } declaration } }] }
                   && HasStorage(declaration.ModifierTokens, "out") && !HasStorage(declaration.ModifierTokens, "patch");

            if (perVertexOutput)
            {
                requirement.Report(GlslRuleDescriptors.NotWritable, at, $"'{array.Name}' の要素", "頂点ごとの出力は gl_InvocationID で引いた要素にしか書き込めません");
            }
        }

        /// <summary>名前が指す変数の、書き込めない理由と writeonly。分からなければ <see langword="null"/>。</summary>
        /// <param name="identifier">名前。</param>
        /// <param name="member">名前のあるブロックのインスタンスなら、引いたメンバーの名前。</param>
        private Base? Resolve(IdentifierExpressionSyntax identifier, string? member)
        {
            string name = identifier.Name;

            NameResolution resolution = _binder.ResolveName(identifier);

            // 宣言し直していない組み込みの変数は、表の記憶域で決める (入力、定数、uniform)。構成によって記憶域が違うものは見ない。
            // 表は、特別な記憶域の出力 (gl_Position、gl_FragColor、gl_FragDepth など) も in と書いている。in は入力と分かっている名前だけを信じる。
            if (name.StartsWith("gl_", StringComparison.Ordinal))
            {
                return resolution.Kind == DeclaredNameKind.NotFound
                       && compilation.TypeSystem.ForTree(program.Tree) is GlslTypeSystem types
                       && types.Builtins.GetVariables(name) is [{ } first, ..] variables
                       && variables.All(v => v.Storage == first.Storage)
                       && first.Storage switch
                       {
                           "in" when BuiltinInputs.Contains(name) => "ステージの入力",
                           "const" => "定数",
                           "uniform" => "uniform",
                           _ => null,
                       } is { } builtin
                    ? new Base(name, builtin, WriteOnly: false)
                    : null;
            }

            switch (resolution)
            {
                case { Kind: DeclaredNameKind.Local, Candidates: [{ Declarator: ParameterSyntax parameter }] }:
                    return new Base(name, parameter.ModifierTokens.Any(t => t.Text == "const") ? "const の仮引数" : null, WriteOnly: false);

                case { Kind: DeclaredNameKind.Local, Candidates: [{ Declarator: VariableDeclaratorSyntax { Parent: VariableDeclarationSyntax declaration } }] }:
                    return new Base(name, declaration.HasModifier("const") ? "const の変数" : null, WriteOnly: false);

                case { Kind: DeclaredNameKind.Global, Candidates: [{ Declarator: VariableDeclaratorSyntax { Parent: VariableDeclarationSyntax declaration } }] }:
                    return declaration.Parent switch
                    {
                        CompilationUnitSyntax => Classify(name, declaration.ModifierTokens, isBlock: false),
                        InterfaceBlockDeclarationSyntax block => Classify(name, [.. block.Modifiers, .. declaration.ModifierTokens], isBlock: true),
                        _ => null,
                    };

                // インスタンスの名前を持つブロック。名前の解決に載らない。
                case { Kind: DeclaredNameKind.NotFound } when FindBlockInstance(name) is { } block:
                    ImmutableArray<ShaderSyntaxToken> memberModifiers = member is null
                        ? []
                        : block.Fields.FirstOrDefault(f => f.Variables.Any(v => v.Name == member))?.ModifierTokens ?? [];
                    return Classify(name, [.. block.Modifiers, block.Keyword, .. memberModifiers], isBlock: true);

                default:
                    return null;
            }
        }

        /// <summary>修飾子から、書き込めない理由と writeonly を決める。</summary>
        private Base Classify(string name, ImmutableArray<ShaderSyntaxToken> modifiers, bool isBlock)
        {
            GlslShaderStage? stage = requirement.Stage;
            string? reason =
                HasStorage(modifiers, "const") ? "const の変数"
                : HasStorage(modifiers, "uniform") ? "uniform"
                : HasStorage(modifiers, "in") || HasStorage(modifiers, "attribute") || HasStorage(modifiers, "varying") && stage != GlslShaderStage.Vertex ? "ステージの入力"
                : HasStorage(modifiers, "buffer") && HasStorage(modifiers, "readonly") ? "readonly の buffer"
                : HasStorage(modifiers, "buffer") && modifiers.Any(m => m.Text is "shaderRecordEXT" or "shaderRecordNV") ? "shaderRecordEXT の buffer"
                : (HasStorage(modifiers, "hitAttributeEXT") || HasStorage(modifiers, "hitAttributeNV")) && stage != GlslShaderStage.Intersection ? "交差のシェーダーでない所の hitAttributeEXT"
                : HasStorage(modifiers, "taskPayloadSharedEXT") && stage == GlslShaderStage.Mesh ? "メッシュシェーダーの taskPayloadSharedEXT"
                : null;

            return new Base(name, reason, WriteOnly: isBlock && HasStorage(modifiers, "buffer") && HasStorage(modifiers, "writeonly"));
        }

        /// <summary>名前のあるブロックのうち、そのインスタンスの名前を持つもの。</summary>
        private StructDeclarationSyntax? FindBlockInstance(string name)
            => program.Tree.Root.Declarations.OfType<StructDeclarationSyntax>().FirstOrDefault(block =>
                block.Keyword.Text != "struct" && block.Declarators.Any(d => d.Node is VariableDeclaratorSyntax declarator && declarator.Name == name));

        /// <summary>関数の仮引数。</summary>
        private static List<ParameterSyntax> ParametersOf(FunctionDeclarationSyntax function)
            => [.. function.Parameters.Select(p => p.Node).OfType<ParameterSyntax>()];
    }

    /// <summary>括弧を外す。</summary>
    private static ExpressionSyntax Strip(ExpressionSyntax expression)
    {
        while (expression is ParenthesizedExpressionSyntax parenthesized)
        {
            expression = parenthesized.Expression;
        }

        return expression;
    }

    /// <summary>修飾子 (<c>layout(…)</c> の外) に、その修飾子があるか。</summary>
    private static bool HasStorage(ImmutableArray<ShaderSyntaxToken> modifiers, string storage)
    {
        int depth = 0;

        foreach (ShaderSyntaxToken modifier in modifiers)
        {
            depth += modifier.Kind switch
            {
                ShaderSyntaxKind.OpenParenToken => 1,
                ShaderSyntaxKind.CloseParenToken => -1,
                _ => 0,
            };

            if (depth == 0 && modifier.Text == storage)
            {
                return true;
            }
        }

        return false;
    }
}
