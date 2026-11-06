using System.Collections.Immutable;
using Shaderlyn.CFamily.Syntax;
using Shaderlyn.Core.Analysis;
using Shaderlyn.Core.Diagnostics;
using Shaderlyn.Godot;
using Shaderlyn.Godot.Parsing;
using Shaderlyn.Semantics;
using Shaderlyn.Semantics.Programs;
using Shaderlyn.Semantics.Symbols;

namespace Shaderlyn.Rules.Godot;

/// <summary>
/// Godot シェーダー言語の、ステージで使えないもの (GD0002)、ステージ関数の形 (GD0322)、varying の補間の修飾子 (GD0101) を検査する。
/// </summary>
/// <remarks>
/// <para>
/// フラグメントだけの組み込み関数 (<c>dFdx</c> など、<see cref="GodotBuiltins.FragmentOnlyFunctions"/>) は、
/// <c>spatial</c> / <c>canvas_item</c> / <c>sky</c> でだけ使える。ほかの種類では、どこで使っても誤りである。
/// <c>discard</c> は <c>spatial</c> / <c>canvas_item</c> でだけ使え、<c>vertex</c> の中では使えない。
/// </para>
/// <para>
/// Godot 4.7.2 は関数を書いた順に読み、関数ごとに「vertex で使えないもの」(フラグメントだけの組み込み関数、ステージ関数の中でだけ使える組み込み関数
/// (<c>texture_sdf</c> など)、<c>discard</c>、varying) を使った順に覚える。<c>vertex</c> の中で関数を呼ぶたびに、<c>vertex</c> と、
/// <c>vertex</c> から (間接的にでも) 呼ぶ利用者の関数の、最初に覚えたものを調べる (<c>_validate_restricted_func</c>)。
/// 最初のものが <c>vertex</c> で書き込んだ varying なら通り、それ以外なら誤りである。ここでも同じ順にたどる。
/// </para>
/// <para>
/// varying は、最初に書き込んだ関数で決まる (<c>_validate_varying_assign</c>)。書き込めるのは <c>vertex</c> と <c>fragment</c> だけで、
/// <c>vertex</c> で書き込んだものは <c>fragment</c> で、<c>fragment</c> で書き込んだものは <c>vertex</c> で書き換えられない。
/// bool の varying は <c>fragment</c> で書き込んでから使う。out / inout の仮引数に渡せるかも、その時点の状態で決まる。
/// 整数の varying は、<c>fragment</c> で書き込んだものでなければ <c>flat</c> が要る。
/// </para>
/// <para>
/// varying の数 (行列は列の数、配列は要素の数) は、レンダラーの組み込みの varying の分 (<see cref="GodotRenderers.BaseVaryingIndex"/>) と合わせて
/// <see cref="GodotRenderers.MaxVaryings"/> までである (GD0104)。Godot と同じく、宣言した順に数えて、最初に超えた varying で報告する。
/// </para>
/// <para>
/// ステージ関数 (そのシェーダーの種類の <c>vertex</c> / <c>fragment</c> など) は、仮引数を持てず、戻り値は <c>void</c> である。
/// ほかの種類のステージ関数の名前 (<c>particles</c> の <c>fragment</c>) は、ただの利用者の関数である。
/// </para>
/// </remarks>
internal sealed class GodotStageAnalyzer : SemanticRuleAnalyzer
{
    /// <summary>フラグメントだけの組み込み関数を使えるシェーダーの種類 (<c>is_supported_frag_only_funcs</c>)。</summary>
    private static readonly ImmutableHashSet<string> FragmentFunctionShaderTypes = ["spatial", "canvas_item", "sky"];

    /// <summary><c>discard</c> を使えるシェーダーの種類 (<c>is_discard_supported</c>)。</summary>
    private static readonly ImmutableHashSet<string> DiscardShaderTypes = ["spatial", "canvas_item"];

    /// <summary>bool の型 (Godot の <c>TYPE_INT</c> より前)。</summary>
    private static readonly ImmutableHashSet<string> BooleanTypes = ["bool", "bvec2", "bvec3", "bvec4"];

    /// <summary>整数の型。</summary>
    private static readonly ImmutableHashSet<string> IntegerTypes = ["int", "ivec2", "ivec3", "ivec4", "uint", "uvec2", "uvec3", "uvec4"];

    /// <inheritdoc/>
    protected override bool AnalyzesGodot => true;

    /// <inheritdoc/>
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
    [
        GodotRuleDescriptors.NotAvailableInStage, GodotRuleDescriptors.StageFunctionSignature,
        GodotRuleDescriptors.QualifierNotAllowed, GodotRuleDescriptors.InvalidOutArgument, GodotRuleDescriptors.TooManyVaryings,
    ];

    /// <inheritdoc/>
    protected override void Analyze(SyntaxTreeAnalysisContext context, ShaderCompilation compilation)
    {
        if (!compilation.IsGodot)
        {
            return;
        }

        HashSet<(string? File, int Start)> reported = [];

        void Report(DiagnosticDescriptor descriptor, ShaderSyntaxToken token, string message)
        {
            if (compilation.IsReportable(token)
                && token.GetLocation() is { } location
                && reported.Add((location.FilePath, location.Span.Start)))
            {
                context.ReportDiagnostic(Diagnostic.Create(descriptor, location, message));
            }
        }

        foreach (AnalyzedProgram program in compilation.Programs)
        {
            if (compilation.TypeSystem.ForTree(program.Tree) is not GodotTypeSystem types
                || program.Tree.Language is not GodotLanguageOptions { ShaderType: { } shaderType } language)
            {
                continue;
            }

            ImmutableArray<FunctionDeclarationSyntax> functions = [.. program.Tree.Root.Declarations.OfType<FunctionDeclarationSyntax>().Where(f => f.Body is not null)];
            CheckSignatures(functions, types, Report);
            new Walker(program, compilation.GetExpressionTypeBinder(program), types, shaderType, Report).Run(functions);
            CheckVaryingCount(program, shaderType, language.Renderer, Report);
        }
    }

    /// <summary>varying の数を、宣言した順に数える (Godot の <c>_parse_shader</c> の終わりの検査)。</summary>
    private static void CheckVaryingCount(
        AnalyzedProgram program,
        string shaderType,
        GodotRenderer renderer,
        Action<DiagnosticDescriptor, ShaderSyntaxToken, string> report)
    {
        int baseIndex = renderer.BaseVaryingIndex(shaderType);
        long used = baseIndex;

        foreach (VariableDeclarationSyntax declaration in program.Tree.Root.Declarations.OfType<VariableDeclarationSyntax>().Where(d => d.HasModifier("varying")))
        {
            foreach (VariableDeclaratorSyntax declarator in declaration.Variables)
            {
                used += VaryingSize(declaration.Type, declarator);

                if (used > GodotRenderers.MaxVaryings)
                {
                    string builtin = baseIndex > 0 ? $"{renderer.DisplayName()} のレンダラーの {shaderType} のシェーダーでは組み込みの varying が {baseIndex} を使い、" : string.Empty;
                    report(GodotRuleDescriptors.TooManyVaryings, declarator.NameToken,
                        $"varying が多すぎます。{builtin}'{declarator.Name}' までで {used} になり、上限の {GodotRenderers.MaxVaryings} を超えます。");
                    return;
                }
            }
        }
    }

    /// <summary>varying 1 つが使う数 (<c>ShaderNode::Varying::get_size</c>)。行列は列の数、配列は要素の数を掛ける。</summary>
    /// <remarks>配列の大きさが整数のリテラルでなければ 1 とする (少なく数えて、誤報告しない)。</remarks>
    private static long VaryingSize(TypeSyntax type, VariableDeclaratorSyntax declarator)
    {
        ImmutableArray<ShaderSyntaxToken> ranks = declarator.ArrayRankTokens.IsEmpty ? type.ArrayRanks : declarator.ArrayRankTokens;
        long size = ranks is [{ Kind: ShaderSyntaxKind.OpenBracketToken }, var length, { Kind: ShaderSyntaxKind.CloseBracketToken }]
            && GodotLiterals.TryParseInteger(length.Text, out long value) && value > 0
            ? value
            : 1;

        return type.Name switch
        {
            "mat2" => size * 2,
            "mat3" => size * 3,
            "mat4" => size * 4,
            _ => size,
        };
    }

    /// <summary>ステージ関数の仮引数と戻り値を調べる。</summary>
    private static void CheckSignatures(
        ImmutableArray<FunctionDeclarationSyntax> functions,
        GodotTypeSystem types,
        Action<DiagnosticDescriptor, ShaderSyntaxToken, string> report)
    {
        foreach (FunctionDeclarationSyntax function in functions.Where(f => types.IsStageFunction(f.Name)))
        {
            if (function.ParameterList.Any())
            {
                report(GodotRuleDescriptors.StageFunctionSignature, function.NameToken, $"ステージ関数 '{function.Name}' は仮引数を持てません。");
            }
            else if (function.ReturnType.Name != "void")
            {
                report(GodotRuleDescriptors.StageFunctionSignature, function.NameToken, $"ステージ関数 '{function.Name}' の戻り値は void でなければなりません。");
            }
        }
    }

    /// <summary>varying を最初に書き込んだステージ (<c>ShaderNode::Varying::Stage</c>)。</summary>
    private enum VaryingStage
    {
        Unknown,
        Vertex,
        Fragment,
    }

    /// <summary>varying 1 つの状態。</summary>
    private sealed class Varying(VariableDeclarationSyntax declaration, VariableDeclaratorSyntax declarator)
    {
        public VariableDeclaratorSyntax Declarator { get; } = declarator;

        public string Type { get; } = declaration.Type.Name;

        public bool IsFlat { get; } = declaration.HasModifier("flat");

        public VaryingStage Stage { get; set; }
    }

    /// <summary>関数 1 つの、呼んだ利用者の関数と、vertex で使えないものを使った順。</summary>
    private sealed class FunctionInfo(string name)
    {
        public string Name { get; } = name;

        public List<string> Calls { get; } = [];

        public List<(ShaderSyntaxToken Token, string Name, Varying? Varying)> Items { get; } = [];
    }

    /// <summary>関数を書いた順にたどり、Godot の読む順に状態を変えながら調べる。</summary>
    private sealed class Walker(
        AnalyzedProgram program,
        ExpressionTypeBinder binder,
        GodotTypeSystem types,
        string shaderType,
        Action<DiagnosticDescriptor, ShaderSyntaxToken, string> report)
    {
        private readonly bool _fragmentFunctions = FragmentFunctionShaderTypes.Contains(shaderType);
        private readonly bool _discard = DiscardShaderTypes.Contains(shaderType);
        private readonly HashSet<string> _stageOnly = [.. GodotBuiltins.Instance.StageFunctions.Where(f => f.ShaderType == shaderType).Select(f => f.Function.Name)];
        private readonly Dictionary<string, FunctionInfo> _infos = new(StringComparer.Ordinal);
        private readonly Dictionary<VariableDeclaratorSyntax, Varying> _varyings = new(ReferenceEqualityComparer.Instance);
        private ImmutableArray<FunctionDeclarationSyntax> _functions = [];
        private FunctionInfo _current = new(string.Empty);

        public void Run(ImmutableArray<FunctionDeclarationSyntax> functions)
        {
            _functions = functions;

            foreach (VariableDeclarationSyntax declaration in program.Tree.Root.Declarations.OfType<VariableDeclarationSyntax>().Where(d => d.HasModifier("varying")))
            {
                foreach (VariableDeclaratorSyntax declarator in declaration.Variables)
                {
                    _varyings[declarator] = new Varying(declaration, declarator);
                }
            }

            foreach (FunctionDeclarationSyntax function in functions)
            {
                _current = _infos[function.Name] = new FunctionInfo(function.Name);
                Walk(function.Body!);
            }

            // 整数の varying は、fragment で書き込んだものでなければ flat が要る。
            foreach (Varying varying in _varyings.Values)
            {
                if (varying.Stage != VaryingStage.Fragment && IntegerTypes.Contains(varying.Type) && !varying.IsFlat)
                {
                    report(GodotRuleDescriptors.QualifierNotAllowed, varying.Declarator.NameToken,
                        $"整数の varying '{varying.Declarator.Name}' には flat が要ります (fragment 関数で書き込むものを除く)。");
                }
            }
        }

        private void Walk(ShaderSyntaxNode node)
        {
            switch (node)
            {
                case JumpStatementSyntax { Keyword.Text: "discard" } jump:
                    if (!_discard)
                    {
                        report(GodotRuleDescriptors.NotAvailableInStage, jump.Keyword, $"discard は、{shaderType} のシェーダーでは使えません。");
                        return;
                    }

                    if (_current.Name == "vertex" && types.IsStageFunction("vertex"))
                    {
                        report(GodotRuleDescriptors.NotAvailableInStage, jump.Keyword, "discard は vertex 関数の中では使えません。");
                    }

                    _current.Items.Add((jump.Keyword, "discard", null));
                    return;

                case IdentifierExpressionSyntax identifier when VaryingOf(identifier) is { } varying:
                    UseVarying(identifier, varying);
                    return;

                case InvocationExpressionSyntax { Target: IdentifierExpressionSyntax callee } invocation:
                    foreach (ExpressionSyntax argument in invocation.ArgumentExpressions)
                    {
                        Walk(argument);
                    }

                    Call(invocation, callee);
                    return;
            }

            foreach (Core.Syntax.SyntaxNode child in node.ChildNodes())
            {
                if (child is ShaderSyntaxNode shaderNode)
                {
                    Walk(shaderNode);
                }
            }

            // v.x = … と v++ は、式を読み終えてから書き込みを調べる (Godot の _validate_assign)。
            ExpressionSyntax? target = node switch
            {
                AssignmentExpressionSyntax assignment => assignment.Left,
                PrefixUnaryExpressionSyntax { OperatorToken.Kind: ShaderSyntaxKind.PlusPlusToken or ShaderSyntaxKind.MinusMinusToken } prefix => prefix.Operand,
                PostfixUnaryExpressionSyntax postfix => postfix.Operand,
                _ => null,
            };

            if (target is not null && !IsDirectTarget(target) && RootOf(target) is IdentifierExpressionSyntax root && VaryingOf(root) is { } assigned)
            {
                Assign(assigned, root.Identifier);
            }
        }

        /// <summary>varying の名前を使った。</summary>
        private void UseVarying(IdentifierExpressionSyntax identifier, Varying varying)
        {
            _current.Items.Add((identifier.Identifier, identifier.Name, varying));

            if (IsDirectTarget(identifier))
            {
                Assign(varying, identifier.Identifier);
            }
            else if (varying.Stage == VaryingStage.Unknown && BooleanTypes.Contains(varying.Type))
            {
                report(GodotRuleDescriptors.NotAvailableInStage, identifier.Identifier, _current.Name == "vertex"
                    ? $"{varying.Type} の varying '{identifier.Name}' は fragment 関数でだけ使えます。"
                    : $"varying '{identifier.Name}' は、先に fragment 関数で書き込みます。");
            }
        }

        /// <summary>varying に書き込んだ (<c>_validate_varying_assign</c>)。</summary>
        private void Assign(Varying varying, ShaderSyntaxToken at)
        {
            string function = _current.Name;

            if (function is not ("vertex" or "fragment"))
            {
                report(GodotRuleDescriptors.NotAvailableInStage, at, $"varying '{varying.Declarator.Name}' には、{function} 関数の中では書き込めません (vertex と fragment だけ)。");
                return;
            }

            switch (varying.Stage)
            {
                case VaryingStage.Unknown when function == "vertex":
                    if (BooleanTypes.Contains(varying.Type))
                    {
                        report(GodotRuleDescriptors.NotAvailableInStage, at, $"{varying.Type} の varying '{varying.Declarator.Name}' は fragment 関数でだけ書き込めます。");
                        return;
                    }

                    varying.Stage = VaryingStage.Vertex;
                    break;

                case VaryingStage.Unknown:
                    varying.Stage = VaryingStage.Fragment;
                    break;

                case VaryingStage.Vertex when function == "fragment":
                    report(GodotRuleDescriptors.NotAvailableInStage, at, $"vertex 関数で書き込んだ varying '{varying.Declarator.Name}' は、fragment と light では書き換えられません。");
                    break;

                case VaryingStage.Fragment when function == "vertex":
                    report(GodotRuleDescriptors.NotAvailableInStage, at, $"fragment 関数で書き込んだ varying '{varying.Declarator.Name}' は、vertex と light では書き換えられません。");
                    break;
            }
        }

        /// <summary>関数を呼んだ。</summary>
        private void Call(InvocationExpressionSyntax invocation, IdentifierExpressionSyntax callee)
        {
            string name = callee.Name;

            // 値の作成 (vec3(…)) は関数の呼び出しでない。
            if (GodotKeywords.BuiltInTypes.Contains(name) || name == _current.Name)
            {
                return;
            }

            CheckVaryingArguments(invocation, name);

            bool builtin = false;

            if (_infos.ContainsKey(name))
            {
                _current.Calls.Add(name);
            }
            else if (_fragmentFunctions && _stageOnly.Contains(name))
            {
                _current.Items.Add((callee.Identifier, name, null));
                builtin = true;
            }
            else if (GodotBuiltins.Instance.FragmentOnlyFunctions.Contains(name))
            {
                if (!_fragmentFunctions)
                {
                    report(GodotRuleDescriptors.NotAvailableInStage, callee.Identifier, $"組み込み関数 '{name}' は、{shaderType} のシェーダーでは使えません。");
                    return;
                }

                _current.Items.Add((callee.Identifier, name, null));
                builtin = true;
            }

            if (_fragmentFunctions && _current.Name == "vertex" && types.IsStageFunction("vertex"))
            {
                Validate(name, _current, builtin);
            }
        }

        /// <summary>vertex と、vertex から呼ぶ関数の、最初に使った「vertex で使えないもの」を調べる (<c>_validate_restricted_func</c>)。</summary>
        private bool Validate(string name, FunctionInfo info, bool builtin)
        {
            bool inVertex = info.Name == "vertex";

            if (!builtin)
            {
                foreach (string callee in info.Calls)
                {
                    if ((inVertex && callee != name) || !_infos.TryGetValue(callee, out FunctionInfo? called))
                    {
                        continue;
                    }

                    if (!Validate(name, called, builtin: false))
                    {
                        return false;
                    }
                }
            }

            if (info.Items.Count == 0)
            {
                return true;
            }

            (ShaderSyntaxToken token, string item, Varying? varying) = info.Items[0];

            if (varying is { Stage: VaryingStage.Vertex })
            {
                return true;
            }

            report(GodotRuleDescriptors.NotAvailableInStage, token, inVertex
                ? $"'{item}' は vertex 関数の中では使えません。"
                : $"'{item}' はここでは使えません。関数 '{info.Name}' は vertex 関数から呼ばれています。");
            return false;
        }

        /// <summary>varying を out / inout / in の仮引数に渡せるかを、その時点の状態で調べる。</summary>
        private void CheckVaryingArguments(InvocationExpressionSyntax invocation, string name)
        {
            ImmutableArray<ExpressionSyntax> arguments = [.. invocation.ArgumentExpressions];
            FunctionDeclarationSyntax[] candidates = [.. _functions.Where(f => f.Name == name && f.ParameterList.Count() == arguments.Length)];

            for (int i = 0; i < arguments.Length; i++)
            {
                ExpressionSyntax argument = arguments[i] is ElementAccessExpressionSyntax element ? element.Target : arguments[i];

                if (argument is not IdentifierExpressionSyntax identifier || VaryingOf(identifier) is not { } varying)
                {
                    continue;
                }

                if (candidates.Length == 0)
                {
                    if (GodotBuiltins.Instance.OutArguments.TryGetValue(name, out ImmutableArray<int> outs) && outs.Contains(i))
                    {
                        report(GodotRuleDescriptors.InvalidOutArgument, identifier.Identifier, $"varying '{identifier.Name}' は組み込み関数の out の仮引数に渡せません。");
                    }

                    continue;
                }

                string[] qualifiers = [.. candidates.Select(f => QualifierOf(f.ParameterList.ElementAt(i))).Distinct()];

                if (qualifiers is not [var qualifier])
                {
                    continue;
                }

                bool output = qualifier is "out" or "inout";
                string function = _current.Name;
                bool error = varying.Stage switch
                {
                    VaryingStage.Unknown => output,
                    VaryingStage.Vertex => output && function != "vertex",
                    VaryingStage.Fragment => output ? function != "fragment" : function is not ("fragment" or "light"),
                    _ => false,
                };

                if (error)
                {
                    report(GodotRuleDescriptors.NotAvailableInStage, identifier.Identifier, $"varying '{identifier.Name}' は、ここでは {qualifier} の仮引数に渡せません。");
                }
            }

            static string QualifierOf(ParameterSyntax parameter)
                => parameter.ModifierTokens.FirstOrDefault(t => t.Text is "out" or "inout")?.Text ?? "in";
        }

        /// <summary>名前が varying を指していれば、その varying。</summary>
        private Varying? VaryingOf(IdentifierExpressionSyntax identifier)
        {
            if (_varyings.Count == 0)
            {
                return null;
            }

            NameResolution resolution = binder.ResolveName(identifier);

            return resolution is { Kind: DeclaredNameKind.Global, Candidates: [{ Declarator: VariableDeclaratorSyntax declarator }] }
                   && _varyings.TryGetValue(declarator, out Varying? varying)
                ? varying
                : null;
        }

        /// <summary>
        /// Godot が名前を読んだときに書き込みと見なす形か (名前、か配列の varying の <c>v[i]</c> のすぐ後ろが代入の演算子)。
        /// </summary>
        private static bool IsDirectTarget(ExpressionSyntax expression)
        {
            ExpressionSyntax target = expression.Parent is ElementAccessExpressionSyntax element && element.Target == expression ? element : expression;

            return target.Parent is AssignmentExpressionSyntax assignment && assignment.Left == target;
        }

        /// <summary>添字・メンバー・括弧を外した元の式。</summary>
        private static ExpressionSyntax RootOf(ExpressionSyntax expression)
        {
            while (true)
            {
                switch (expression)
                {
                    case ParenthesizedExpressionSyntax parenthesized:
                        expression = parenthesized.Expression;
                        continue;
                    case ElementAccessExpressionSyntax element:
                        expression = element.Target;
                        continue;
                    case MemberAccessExpressionSyntax member:
                        expression = member.Target;
                        continue;
                    default:
                        return expression;
                }
            }
        }
    }
}
